import os
import asyncio
import shutil
from fastapi import APIRouter, HTTPException

from app.config import settings
from app.services.doc_search import doc_search

router = APIRouter()


@router.post("/{kb_id}/refresh")
async def refresh_kb(kb_id: str):
    """Placeholder for refreshing a KB from its source. Not yet implemented."""
    return {"kbId": kb_id, "updated": True, "rowCount": None}


def _delete_kb_files(kb_id: str) -> bool:
    """Synchronous KB file deletion — runs in thread pool."""
    deleted = False

    # Check domain directories
    domain_dir = os.path.join(settings.data_dir, "domain")
    if os.path.exists(domain_dir):
        for owner in os.listdir(domain_dir):
            kb_path = os.path.join(domain_dir, owner, kb_id)
            if os.path.exists(kb_path):
                shutil.rmtree(kb_path)
                deleted = True

    # Check session directories
    session_dir = os.path.join(settings.data_dir, "session")
    if os.path.exists(session_dir):
        for owner in os.listdir(session_dir):
            kb_path = os.path.join(session_dir, owner, kb_id)
            if os.path.exists(kb_path):
                shutil.rmtree(kb_path)
                deleted = True

    return deleted


@router.delete("/{kb_id}")
async def delete_kb(kb_id: str):
    """
    Delete a KB's data files (Parquet or FTS index).
    Searches all scope directories for the KB ID.
    """
    deleted = await asyncio.to_thread(_delete_kb_files, kb_id)

    # Check docs (FTS index) — doc_search.delete is sync
    doc_deleted = await asyncio.to_thread(doc_search.delete, kb_id)
    deleted = deleted or doc_deleted

    if not deleted:
        raise HTTPException(404, f"KB not found: {kb_id}")

    return {"deleted": True}
