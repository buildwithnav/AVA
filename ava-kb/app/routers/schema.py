import asyncio
from fastapi import APIRouter, HTTPException

from app.models.schemas import SchemaRequest
from app.services.file_handler import get_parquet_schema
from app.services.doc_search import doc_search

router = APIRouter()


@router.post("/schema")
async def get_schema(request: SchemaRequest):
    """
    Get schema information for a knowledge base.
    For datasets: columns, types, row count, sample rows.
    For documents: document count, chunk count, sample titles.
    """
    if request.type == "dataset":
        try:
            info = await asyncio.to_thread(get_parquet_schema, request.path)
        except Exception as e:
            raise HTTPException(404, f"Could not read schema: {str(e)}")

        return {
            "kbId": request.knowledge_base_id,
            "type": "dataset",
            "columns": info["columns"],
            "rowCount": info["rowCount"],
            "sampleRows": info["sampleRows"],
        }

    elif request.type == "document":
        info = await asyncio.to_thread(doc_search.get_info, request.knowledge_base_id)
        if not info:
            raise HTTPException(404, f"Document KB not found: {request.knowledge_base_id}")

        return {
            "kbId": request.knowledge_base_id,
            "type": "document",
            "documentCount": info["documentCount"],
            "chunkCount": info["chunkCount"],
            "sampleTitles": info["sampleTitles"],
        }

    else:
        raise HTTPException(400, f"Unknown KB type: {request.type}")
