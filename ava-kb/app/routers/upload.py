import os
import sys
import time
import asyncio
import logging
import tempfile
try:
    import resource  # Unix-only; used for peak-RSS diagnostics
except ImportError:  # Windows has no 'resource' module
    resource = None
from email.header import decode_header as _decode_header
from fastapi import APIRouter, UploadFile, File, Form, HTTPException

from app.config import settings
from app.services.file_handler import parse_and_store, parse_xlsx_path
from app.services.chunker import chunk_text, chunk_document, chunk_qa_pairs
from app.services.doc_search import doc_search
from app.services.text_extractor import extract_pdf_text, extract_docx_text, extract_xlsx_text

# Self-contained logger that prints to stdout so request activity is visible in
# pod logs even under gunicorn/uvicorn (which otherwise leave this service silent).
logger = logging.getLogger("ava-kb")
if not logger.handlers:
    _handler = logging.StreamHandler(sys.stdout)
    _handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s [ava-kb] %(message)s"))
    logger.addHandler(_handler)
    logger.setLevel(logging.INFO)
    logger.propagate = False

# Cap concurrent dataset parses. Parsing a large spreadsheet is the memory
# hot-spot in this service, so serializing it (default 1) prevents two big
# refreshes from stacking their peaks and OOM-killing the worker.
_PARSE_SEMAPHORE = asyncio.Semaphore(int(os.getenv("KB_PARSE_CONCURRENCY", "1")))

router = APIRouter()


def _decode_filename(raw: str) -> str:
    """Decode RFC 2047 MIME-encoded filename (=?utf-8?B?...?=) if present."""
    parts = _decode_header(raw)
    decoded = []
    for part, charset in parts:
        if isinstance(part, bytes):
            decoded.append(part.decode(charset or "utf-8", errors="replace"))
        else:
            decoded.append(part)
    return "".join(decoded)


@router.post("/upload")
async def upload_file(
    file: UploadFile = File(...),
    kbId: str = Form(...),
    scope: str = Form(...),
    sessionId: str = Form(None),
    domainId: str = Form(None),
    type: str = Form("dataset"),
    append_mode: str = Form("false"),
    chunk_size: int = Form(None),
    chunk_overlap: int = Form(None),
):
    """Upload a file (CSV/Excel) and convert to Parquet or FTS index."""
    logger.info(
        "upload: ENTRY kbId=%s scope=%s type=%s sessionId=%s domainId=%s append=%s raw_filename=%r",
        kbId, scope, type, sessionId, domainId, append_mode, file.filename,
    )
    # Determine format from filename — decode RFC 2047 if .NET MIME-encoded it
    filename = _decode_filename(file.filename or "")
    if filename.endswith(".csv"):
        fmt = "csv"
    elif filename.endswith((".xlsx", ".xls")):
        fmt = "xlsx"
    elif filename.endswith(".json"):
        fmt = "json"
    elif filename.endswith((".txt", ".md")):
        fmt = "text"
    elif filename.endswith(".pdf"):
        fmt = "pdf"
    elif filename.endswith(".docx"):
        fmt = "docx"
    else:
        raise HTTPException(400, f"Unsupported file type: {filename}")

    # Dataset spreadsheets are streamed to a temp file in the dataset section
    # below, so we avoid buffering the whole payload here. Everything else
    # (documents, csv, json) reads the bytes up front.
    is_dataset_xlsx = type != "document" and fmt == "xlsx"
    file_bytes = b""
    if not is_dataset_xlsx:
        file_bytes = await file.read()
        logger.info(
            "upload: received kbId=%s filename=%s fmt=%s size=%.2fMB",
            kbId, filename, fmt, len(file_bytes) / (1024 * 1024),
        )
    if type == "document":
        # Extract text based on format
        if fmt == "pdf":
            text = extract_pdf_text(file_bytes)
        elif fmt == "docx":
            text = extract_docx_text(file_bytes)
        elif fmt == "xlsx":
            text = extract_xlsx_text(file_bytes)
        else:
            text = file_bytes.decode("utf-8", errors="replace")

        if not text or not text.strip():
            raise HTTPException(400, f"No text content could be extracted from {filename}")

        # Use parent-child chunking for documents
        doc_chunks = chunk_document(
            text,
            source=filename,
            parent_max_size=2000,
            child_chunk_size=500,
            child_chunk_overlap=50,
        )
        chunks = doc_chunks.all_chunks

        if append_mode == "true":
            count = await doc_search.append(kbId, chunks)
        else:
            count = await doc_search.ingest(kbId, chunks)
        logger.info("upload: DONE document kbId=%s chunks=%s", kbId, count)
        return {
            "kbId": kbId,
            "type": "document",
            "chunkCount": count,
            "parentCount": len(doc_chunks.parents),
            "childCount": len(doc_chunks.children),
            "chunks": chunks,
        }

    # Dataset files → Parquet
    scope_dir = "session" if scope == "session" else "domain"
    owner_id = sessionId if scope == "session" else domainId
    if not owner_id:
        raise HTTPException(400, "sessionId or domainId is required")

    output_dir = os.path.join(settings.data_dir, scope_dir, owner_id, kbId)
    output_path = os.path.join(output_dir, "data.parquet")

    _t0 = time.perf_counter()
    try:
        if is_dataset_xlsx:
            # Stream the spreadsheet to a temp file so the full payload never
            # sits in memory, then parse from disk under the concurrency cap.
            tmp_fd, tmp_path = tempfile.mkstemp(suffix=".xlsx")
            try:
                # Enforce the size cap WHILE streaming: the pod's ephemeral
                # storage is only 256Mi, so we must not write an oversized temp
                # file before rejecting it. Abort as soon as the limit is passed.
                max_bytes = settings.max_file_size_mb * 1024 * 1024
                size = 0
                with os.fdopen(tmp_fd, "wb") as tmp:
                    while chunk := await file.read(1024 * 1024):
                        size += len(chunk)
                        if size > max_bytes:
                            raise HTTPException(
                                400,
                                f"File exceeds {settings.max_file_size_mb}MB limit",
                            )
                        tmp.write(chunk)
                size_mb = size / (1024 * 1024)
                logger.info(
                    "upload: parsing (streamed) kbId=%s fmt=xlsx size=%.2fMB -> %s",
                    kbId, size_mb, output_path,
                )
                async with _PARSE_SEMAPHORE:
                    result = await asyncio.to_thread(parse_xlsx_path, tmp_path, output_path)
            finally:
                try:
                    os.unlink(tmp_path)
                except OSError:
                    pass
        else:
            logger.info(
                "upload: parsing kbId=%s fmt=%s size=%.2fMB -> %s",
                kbId, fmt, len(file_bytes) / (1024 * 1024), output_path,
            )
            async with _PARSE_SEMAPHORE:
                result = await asyncio.to_thread(parse_and_store, file_bytes, fmt, output_path)
    except ValueError as e:
        logger.warning("upload: parse rejected kbId=%s: %s", kbId, e)
        raise HTTPException(400, str(e))

    _peak_mb = (
        resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024  # type: ignore[union-attr]
        if resource is not None else 0.0
    )
    logger.info(
        "upload: DONE kbId=%s rows=%s in %.1fs peakRSS=%.0fMB -> %s",
        kbId, result["rowCount"], time.perf_counter() - _t0, _peak_mb, output_path,
    )

    return {
        "kbId": kbId,
        "type": "dataset",
        "schema": result["schema"],
        "rowCount": result["rowCount"],
        "sampleRows": result["sampleRows"],
        "dataPath": output_path,
    }


@router.delete("/source")
async def delete_source(kbId: str, source: str):
    """Delete all chunks from a specific source file in a KB's FTS index."""
    result = await doc_search.delete_source(kbId, source)
    if not result:
        raise HTTPException(404, f"Source '{source}' not found in KB '{kbId}'")
    return {"deleted": True, "kbId": kbId, "source": source}
