import os
import asyncio
from fastapi import APIRouter, UploadFile, File, Form, HTTPException, Request

from app.config import settings
from app.services.file_handler import parse_and_store
from app.services.chunker import chunk_text, chunk_qa_pairs
from app.services.doc_search import doc_search
from app.models.schemas import IngestRequest

router = APIRouter()


@router.post("/ingest")
async def ingest_data(request: Request):
    """
    Ingest data from JSON body or multipart form.

    JSON body: { kbId, scope, sessionId/domainId, name, format, data }
    Multipart: file + metadata fields
    """
    content_type = request.headers.get("content-type", "")

    if "multipart" in content_type:
        return await _ingest_multipart(request)
    else:
        return await _ingest_json(request)


async def _ingest_json(request: Request):
    """Handle JSON body ingestion (inline data)."""
    body = await request.json()

    kb_id = body.get("kbId")
    scope = body.get("scope", "session")
    session_id = body.get("sessionId")
    domain_id = body.get("domainId")
    name = body.get("name", kb_id)
    fmt = body.get("format", "json")
    data = body.get("data")
    kb_type = body.get("type", "dataset")

    if not kb_id:
        raise HTTPException(400, "kbId is required")

    if kb_type == "document" and fmt == "json" and data:
        # JSON Q&A pairs → chunk and index
        chunks = chunk_qa_pairs(data, source=name)
        if not chunks:
            # Try as plain text chunks
            text = "\n".join(str(item) for item in data)
            chunks = chunk_text(text, source=name, chunk_size=settings.chunk_size, chunk_overlap=settings.chunk_overlap)
        count = await doc_search.ingest(kb_id, chunks)
        return {"kbId": kb_id, "type": "document", "chunkCount": count}

    # Dataset ingestion
    if not data:
        raise HTTPException(400, "data is required for JSON ingestion")

    scope_dir = "session" if scope == "session" else "domain"
    owner_id = session_id if scope == "session" else domain_id
    if not owner_id:
        raise HTTPException(400, "sessionId or domainId is required")

    output_dir = os.path.join(settings.data_dir, scope_dir, owner_id, kb_id)
    output_path = os.path.join(output_dir, "data.parquet")

    try:
        result = await asyncio.to_thread(parse_and_store, data, fmt, output_path)
    except ValueError as e:
        raise HTTPException(400, str(e))

    return {
        "kbId": kb_id,
        "type": "dataset",
        "schema": result["schema"],
        "rowCount": result["rowCount"],
        "sampleRows": result["sampleRows"],
    }


async def _ingest_multipart(request: Request):
    """Handle multipart form ingestion (file upload from .NET)."""
    form = await request.form()

    kb_id = form.get("kbId")
    scope = form.get("scope", "session")
    session_id = form.get("sessionId")
    domain_id = form.get("domainId")
    name = form.get("name", kb_id)
    fmt = form.get("format", "csv")
    kb_type = form.get("type", "dataset")
    file = form.get("file")

    if not kb_id:
        raise HTTPException(400, "kbId is required")
    if not file:
        raise HTTPException(400, "file is required for multipart ingestion")

    file_bytes = await file.read()

    if kb_type == "document":
        text = file_bytes.decode("utf-8", errors="replace")
        chunks = chunk_text(text, source=name, chunk_size=settings.chunk_size, chunk_overlap=settings.chunk_overlap)
        count = await doc_search.ingest(kb_id, chunks)
        return {"kbId": kb_id, "type": "document", "chunkCount": count}

    # Dataset
    scope_dir = "session" if scope == "session" else "domain"
    owner_id = session_id if scope == "session" else domain_id
    if not owner_id:
        raise HTTPException(400, "sessionId or domainId is required")

    output_dir = os.path.join(settings.data_dir, scope_dir, owner_id, kb_id)
    output_path = os.path.join(output_dir, "data.parquet")

    try:
        result = await asyncio.to_thread(parse_and_store, file_bytes, fmt, output_path)
    except ValueError as e:
        raise HTTPException(400, str(e))

    return {
        "kbId": kb_id,
        "type": "dataset",
        "schema": result["schema"],
        "rowCount": result["rowCount"],
        "sampleRows": result["sampleRows"],
    }
