"""Pydantic models for the ava-kb chunking and search system."""

from typing import Optional
from pydantic import BaseModel


class ChunkBase(BaseModel):
    """Base chunk fields shared by parent and child chunks."""
    id: str
    source: str
    title: str
    content: str


class ParentChunk(ChunkBase):
    """A parent chunk representing a full semantic section of a document."""
    chunk_level: str = "parent"
    chunk_number: int
    section_heading: Optional[str] = None


class ChildChunk(ChunkBase):
    """A child chunk — a retrieval-sized window within a parent section."""
    chunk_level: str = "child"
    chunk_number: int
    parent_id: str


class DocumentChunks(BaseModel):
    """Result of chunking a document into parent-child hierarchy."""
    source: str
    parents: list[ParentChunk]
    children: list[ChildChunk]

    @property
    def all_chunks(self) -> list[dict]:
        """Flat list of all chunks (parents + children) as dicts for DuckDB storage."""
        result = []
        for p in self.parents:
            result.append({
                "id": p.id,
                "source": p.source,
                "title": p.title,
                "content": p.content,
                "parent_id": None,
                "chunk_level": "parent",
                "chunk_number": p.chunk_number,
            })
        for c in self.children:
            result.append({
                "id": c.id,
                "source": c.source,
                "title": c.title,
                "content": c.content,
                "parent_id": c.parent_id,
                "chunk_level": "child",
                "chunk_number": c.chunk_number,
            })
        return result


class SearchResult(BaseModel):
    """A search result with optional parent context."""
    id: str
    source: str
    title: str
    content: str
    score: float
    chunk_level: str = "child"
    parent_id: Optional[str] = None
    parent_content: Optional[str] = None
