import os
import asyncio
import glob as globmod
import duckdb
from fastapi import APIRouter, HTTPException
from fastapi.responses import StreamingResponse

from app.config import settings
from app.models.schemas import QueryRequest
from app.services.query_engine import execute_query, stream_query_csv, QueryValidationError

router = APIRouter()


def _resolve_dataset_paths(dataset_ids: list[str], paths: dict[str, str]) -> dict[str, str]:
    """
    Resolve empty paths by searching the data directory for matching kbId folders.

    Returns a dict mapping each kbId directly to its parquet path so the LLM
    can reference each table by its meaningful knowledge-base ID in SQL
    (e.g. FROM "clinical-data-sites" rather than a generic "dataset_0").
    Hyphens are permitted by SAFE_NAME and DuckDB handles them with double-quoting.
    """
    resolved = {}
    for kb_id in dataset_ids:
        given_path = paths.get(kb_id, "")
        if given_path and os.path.isfile(given_path):
            parquet_path = given_path
        else:
            # Search for parquet file under any scope/owner
            pattern = os.path.join(settings.data_dir, "**", kb_id, "data.parquet")
            matches = globmod.glob(pattern, recursive=True)
            if matches:
                parquet_path = matches[0]
            else:
                raise HTTPException(404, f"Dataset '{kb_id}' not found in data directory")

        resolved[kb_id] = parquet_path

    return resolved


@router.post("/query")
async def query_data(request: QueryRequest):
    """
    Execute a sandboxed SQL query over Parquet-backed datasets.

    The LLM generates SQL; this endpoint validates and executes it
    against in-memory tables loaded from Parquet files.
    """
    if not request.dataset_ids:
        raise HTTPException(400, "datasetIds is required")

    if not request.sql or not request.sql.strip():
        raise HTTPException(400, "sql is required")

    resolved_paths = await asyncio.to_thread(
        _resolve_dataset_paths, request.dataset_ids, request.dataset_paths or {}
    )
    result = await asyncio.to_thread(execute_query, resolved_paths, request.sql)

    # If there's an error key, return it with 200 (structured error for LLM self-healing)
    # The LLM needs to see the error to retry with corrected SQL
    return result


@router.post("/export")
async def export_data(request: QueryRequest):
    """
    Execute a sandboxed SQL query and stream the FULL result set as CSV.

    Same validation and sandbox as /query, but with no 1000-row cap — this backs
    the "Download CSV" link so users get every row the query produces.
    """
    if not request.dataset_ids:
        raise HTTPException(400, "datasetIds is required")

    if not request.sql or not request.sql.strip():
        raise HTTPException(400, "sql is required")

    resolved_paths = await asyncio.to_thread(
        _resolve_dataset_paths, request.dataset_ids, request.dataset_paths or {}
    )

    # Validate up front (outside the generator) so blocked/invalid SQL returns a
    # proper HTTP error instead of a half-streamed body. Pulling the first chunk
    # here also surfaces DuckDB errors (bad SQL, missing/changed dataset) before
    # the response headers are sent.
    try:
        generator = stream_query_csv(resolved_paths, request.sql)
        first_chunk = await asyncio.to_thread(lambda: next(generator, ""))
    except QueryValidationError as e:
        raise HTTPException(400, e.message)
    except duckdb.Error as e:
        raise HTTPException(400, f"Query failed: {e}")

    def _iter():
        # Re-emit the already-fetched header chunk, then the remaining batches.
        if first_chunk:
            yield first_chunk
        yield from generator

    return StreamingResponse(_iter(), media_type="text/csv; charset=utf-8")
