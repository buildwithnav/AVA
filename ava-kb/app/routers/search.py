import re
import asyncio
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel
from typing import Optional

from app.models.schemas import SearchRequest
from app.services.doc_search import doc_search

router = APIRouter()

# Matches markdown headings or short numbered headings that indicate a new section
_SECTION_HEADING_RE = re.compile(r"^(#{1,6}\s|\d+(\.\d+)?\s+\S)", re.MULTILINE)


class SectionLookupRequest(BaseModel):
    knowledge_base_id: str
    section_query: str
    source: Optional[str] = None
    max_follow_chunks: int = 5


@router.post("/search")
async def search_documents(request: SearchRequest):
    """
    Search document chunks using BM25 full-text search.
    Returns ranked results with source references.
    With parent-child chunking, searches children by default for precision.
    """
    results = await asyncio.to_thread(
        doc_search.search,
        kb_id=request.knowledge_base_id,
        query=request.query,
        top_k=request.top_k,
        source_filter=request.source,
    )

    return {
        "chunks": [
            {
                "id": r.get("id", ""),
                "content": r.get("content", ""),
                "source": r.get("source", ""),
                "title": r.get("title", ""),
                "score": r.get("score", 0.0),
                "parentId": r.get("parent_id"),
                "chunkLevel": r.get("chunk_level", "child"),
            }
            for r in results
        ]
    }


@router.get("/parent")
async def get_parent_chunk(kbId: str, parentId: str):
    """
    Get a parent chunk by ID — returns the full semantic section.
    Used for parent-expansion: retrieve children for precision, expand to parent for context.
    """
    result = await asyncio.to_thread(
        doc_search.get_parent,
        kb_id=kbId,
        parent_id=parentId,
    )
    if result is None:
        raise HTTPException(404, f"Parent chunk '{parentId}' not found in KB '{kbId}'")
    return result


@router.post("/search-expanded")
async def search_with_expansion(request: SearchRequest):
    """
    Search children then expand to parent sections.
    Returns child matches with their full parent content attached.
    """
    results = await asyncio.to_thread(
        doc_search.search_with_parent_expansion,
        kb_id=request.knowledge_base_id,
        query=request.query,
        top_k=request.top_k,
        source_filter=request.source,
    )

    return {
        "chunks": [
            {
                "id": r.get("id", ""),
                "content": r.get("content", ""),
                "source": r.get("source", ""),
                "title": r.get("title", ""),
                "score": r.get("score", 0.0),
                "parentId": r.get("parent_id"),
                "chunkLevel": r.get("chunk_level", "child"),
                "parentContent": r.get("parent_content"),
            }
            for r in results
        ]
    }


@router.get("/chunks-by-source")
async def get_chunks_by_source(kbId: str, source: str):
    """
    Get all chunks from a specific source file (no FTS scoring).
    Used by the embedding pipeline after upload to retrieve all chunks for a file.
    """
    results = await asyncio.to_thread(
        doc_search.get_chunks_by_source,
        kb_id=kbId,
        source=source,
    )
    return {"chunks": results}


@router.get("/chunk-by-id")
async def get_chunk_by_id(kbId: str, chunkId: str):
    """
    Get a single chunk by its exact ID.
    Used for sequential navigation (next/previous chunk).
    """
    result = await asyncio.to_thread(
        doc_search.get_chunk_by_id,
        kb_id=kbId,
        chunk_id=chunkId,
    )
    if result is None:
        raise HTTPException(404, f"Chunk '{chunkId}' not found in KB '{kbId}'")
    return result


class AdjacentChunksRequest(BaseModel):
    knowledge_base_id: str
    chunk_id: str
    before: int = 0
    after: int = 3


@router.post("/adjacent-chunks")
async def get_adjacent_chunks(request: AdjacentChunksRequest):
    """
    Get chunks adjacent to a given chunk ID for sequential navigation.
    Returns chunks before and/or after the reference chunk in document order.
    """
    results = await asyncio.to_thread(
        doc_search.get_adjacent_chunks,
        kb_id=request.knowledge_base_id,
        chunk_id=request.chunk_id,
        before=request.before,
        after=request.after,
    )
    return {"chunks": results}


@router.post("/section-lookup")
async def section_lookup(request: SectionLookupRequest):
    """
    Section-aware lookup: finds chunks whose first ~150 chars contain the section query,
    then returns that chunk plus subsequent chunks until the next major heading.
    This solves 'find section X' queries where similarity search fails because
    many chunks mention a topic but only one chunk IS the named section.
    """
    results = await asyncio.to_thread(
        _section_lookup_sync,
        request.knowledge_base_id,
        request.section_query,
        request.source,
        request.max_follow_chunks,
    )
    return {"chunks": results}


def _section_lookup_sync(
    kb_id: str, section_query: str, source: Optional[str], max_follow: int
) -> list[dict]:
    """Find section heading in DuckDB chunks and return the full section."""
    import duckdb
    import os
    from app.config import settings

    db_path = os.path.join(settings.data_dir, "docs", kb_id, "fts.duckdb")
    if not os.path.exists(db_path):
        return []

    conn = duckdb.connect(db_path, read_only=True)
    try:
        query_lower = section_query.lower()

        # Build source filter clause
        source_clause = ""
        base_params = []
        if source:
            source_clause = " WHERE source = ?"
            base_params = [source]

        # Strategy 1 (best): Find chunks where the FIRST LINE matches the section query.
        # After heading-aware chunking, section titles are the first line of their chunk.
        # This also handles markdown-prefixed headings like "## Monitoring Visit Report".
        sql = f"SELECT id, source, title, content FROM docs{source_clause} ORDER BY id"
        heading_chunks = []
        all_rows = conn.execute(sql, base_params).fetchall()

        # Score each chunk: standalone heading line > first-line match > first-150-char match > full content
        scored = []
        for row in all_rows:
            chunk_id, chunk_source, chunk_title, chunk_content = row
            content = chunk_content or ""
            lines = content.split("\n")
            first_line = lines[0].strip().lower() if lines else ""
            first_150 = content[:150].lower()
            full_lower = content.lower()

            # Skip TOC-like entries (very short first line with page numbers)
            if re.match(r"^\d+(\.\d+)*\s*\w+.*\d+$", first_line) and len(first_line) < 50:
                continue

            # Check if query matches a standalone short line (likely a heading)
            # Prefer lines that ARE the query (not containing it within other text)
            has_standalone_heading = False
            heading_line_idx = -1
            for idx, line in enumerate(lines[:5]):  # Only check first 5 lines
                stripped = line.strip().lower()
                # Remove markdown heading prefix for comparison
                clean = re.sub(r"^#{1,6}\s+", "", stripped)
                if clean == query_lower or clean.endswith(query_lower) and len(clean) < len(query_lower) + 15:
                    has_standalone_heading = True
                    heading_line_idx = idx
                    break

            if has_standalone_heading and heading_line_idx == 0:
                scored.append((0, row))  # Best: heading is first line of chunk
            elif has_standalone_heading and heading_line_idx <= 2:
                scored.append((1, row))  # Great: heading is within first few lines
            elif query_lower in first_line:
                scored.append((2, row))  # Good: query in first line (but not standalone)
            elif query_lower in first_150:
                scored.append((3, row))  # OK: query in first 150 chars
            elif query_lower in full_lower:
                scored.append((4, row))  # Fallback: mentioned somewhere

        # Sort by score priority. Only take matches at the best available score level
        # to avoid mixing the target section with unrelated sections that happen to
        # mention the same phrase.
        scored.sort(key=lambda x: x[0])
        if scored:
            best_score = scored[0][0]
            heading_chunks = [s[1] for s in scored if s[0] == best_score][:3]
        else:
            heading_chunks = []

        if not heading_chunks:
            return []

        # For each heading match, gather the section: heading chunk + subsequent
        # chunks until we hit the next major heading or run out.
        results = []
        seen_ids = set()

        for row in heading_chunks:
            chunk_id, chunk_source, chunk_title, chunk_content = row
            if chunk_id in seen_ids:
                continue
            seen_ids.add(chunk_id)

            results.append({
                "id": chunk_id,
                "source": chunk_source,
                "title": chunk_title,
                "content": chunk_content,
                "score": 1.0,
            })

            # Parse chunk number to follow forward
            m = re.match(r"^(.+)#chunk-(\d+)$", chunk_id)
            if not m:
                continue

            file_prefix = m.group(1)
            start_idx = int(m.group(2))

            # Fetch subsequent chunks
            for i in range(start_idx + 1, start_idx + 1 + max_follow):
                next_id = f"{file_prefix}#chunk-{i}"
                if next_id in seen_ids:
                    continue

                next_row = conn.execute(
                    "SELECT id, source, title, content FROM docs WHERE id = ?",
                    [next_id]
                ).fetchone()

                if not next_row:
                    break

                next_content = next_row[3] or ""
                # Stop if we hit a new major section heading
                first_line = next_content.split("\n")[0].strip()
                if first_line and len(first_line) < 80 and _SECTION_HEADING_RE.match(first_line):
                    break

                seen_ids.add(next_id)
                results.append({
                    "id": next_row[0],
                    "source": next_row[1],
                    "title": next_row[2],
                    "content": next_content,
                    "score": 0.9,
                })

        return results
    finally:
        conn.close()
