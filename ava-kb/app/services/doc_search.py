import duckdb
import asyncio
import os
from collections import defaultdict
from typing import Optional

from app.config import settings


class DocumentSearch:
    """DuckDB FTS-based document search with parent-child chunk support."""

    def __init__(self):
        self._write_locks: dict[str, asyncio.Lock] = defaultdict(asyncio.Lock)

    def _db_path(self, kb_id: str) -> str:
        path = os.path.join(settings.data_dir, "docs", kb_id)
        os.makedirs(path, exist_ok=True)
        return os.path.join(path, "fts.duckdb")

    def _rebuild_fts_index(self, db_path: str, has_docs: bool) -> None:
        conn = duckdb.connect(db_path)
        try:
            conn.execute("INSTALL fts; LOAD fts;")
            try:
                conn.execute("PRAGMA drop_fts_index('docs')")
            except Exception:
                pass
        finally:
            conn.close()

        if not has_docs:
            return

        conn = duckdb.connect(db_path)
        try:
            conn.execute("INSTALL fts; LOAD fts;")
            conn.execute(
                "PRAGMA create_fts_index('docs', 'id', 'content', 'title')"
            )
        finally:
            conn.close()

    def _ensure_schema(self, conn) -> None:
        """Create or migrate the docs table to include parent-child columns."""
        conn.execute("""
            CREATE TABLE IF NOT EXISTS docs (
                id TEXT PRIMARY KEY,
                source TEXT,
                title TEXT,
                content TEXT,
                parent_id TEXT,
                chunk_level TEXT DEFAULT 'child',
                chunk_number INTEGER DEFAULT 0
            )
        """)
        # Migrate existing tables that lack new columns
        try:
            cols = [row[0] for row in conn.execute("DESCRIBE docs").fetchall()]
            if "parent_id" not in cols:
                conn.execute("ALTER TABLE docs ADD COLUMN parent_id TEXT")
            if "chunk_level" not in cols:
                conn.execute("ALTER TABLE docs ADD COLUMN chunk_level TEXT DEFAULT 'child'")
            if "chunk_number" not in cols:
                conn.execute("ALTER TABLE docs ADD COLUMN chunk_number INTEGER DEFAULT 0")
        except Exception:
            pass

    def _ingest_sync(self, db_path: str, chunks: list[dict]) -> int:
        """Synchronous DuckDB ingest — runs in thread pool to avoid blocking event loop."""
        conn = duckdb.connect(db_path)
        try:
            self._ensure_schema(conn)
            conn.execute("DELETE FROM docs")
            if chunks:
                conn.executemany(
                    "INSERT INTO docs (id, source, title, content, parent_id, chunk_level, chunk_number) VALUES (?, ?, ?, ?, ?, ?, ?)",
                    [(
                        c["id"], c["source"], c["title"], c["content"],
                        c.get("parent_id"), c.get("chunk_level", "child"),
                        c.get("chunk_number", 0)
                    ) for c in chunks],
                )
        finally:
            conn.close()

        self._rebuild_fts_index(db_path, bool(chunks))
        return len(chunks)

    async def ingest(self, kb_id: str, chunks: list[dict]) -> int:
        """
        Ingest document chunks into a DuckDB FTS index.
        Supports both flat chunks (legacy) and parent-child chunks (new).
        """
        async with self._write_locks[kb_id]:
            db_path = self._db_path(kb_id)
            return await asyncio.to_thread(self._ingest_sync, db_path, chunks)

    def _append_sync(self, db_path: str, chunks: list[dict]) -> int:
        """Append chunks to existing FTS index, replacing only chunks from the same source."""
        if not chunks:
            return 0

        conn = duckdb.connect(db_path)
        try:
            self._ensure_schema(conn)
            source = chunks[0]["source"]
            conn.execute("DELETE FROM docs WHERE source = ?", [source])
            conn.executemany(
                "INSERT INTO docs (id, source, title, content, parent_id, chunk_level, chunk_number) VALUES (?, ?, ?, ?, ?, ?, ?)",
                [(
                    c["id"], c["source"], c["title"], c["content"],
                    c.get("parent_id"), c.get("chunk_level", "child"),
                    c.get("chunk_number", 0)
                ) for c in chunks],
            )
            has_docs = conn.execute("SELECT COUNT(*) FROM docs").fetchone()[0] > 0
        finally:
            conn.close()

        self._rebuild_fts_index(db_path, has_docs)
        return len(chunks)

    async def append(self, kb_id: str, chunks: list[dict]) -> int:
        """
        Append document chunks to an existing FTS index.
        Only replaces chunks from the same source file.
        """
        async with self._write_locks[kb_id]:
            db_path = self._db_path(kb_id)
            return await asyncio.to_thread(self._append_sync, db_path, chunks)

    def _delete_source_sync(self, db_path: str, source: str) -> bool:
        """Delete all chunks from a specific source file and rebuild FTS index."""
        if not os.path.exists(db_path):
            return False

        conn = duckdb.connect(db_path)
        try:
            try:
                deleted = conn.execute(
                    "DELETE FROM docs WHERE source = ? RETURNING id", [source]
                ).fetchall()
            except Exception:
                return False

            if not deleted:
                return False

            has_docs = conn.execute("SELECT COUNT(*) FROM docs").fetchone()[0] > 0
        finally:
            conn.close()

        self._rebuild_fts_index(db_path, has_docs)
        return True

    async def delete_source(self, kb_id: str, source: str) -> bool:
        """Delete all chunks from a specific source file."""
        async with self._write_locks[kb_id]:
            db_path = self._db_path(kb_id)
            return await asyncio.to_thread(self._delete_source_sync, db_path, source)

    def search(self, kb_id: str, query: str, top_k: int = 10, children_only: bool = True, source_filter: str | None = None) -> list[dict]:
        """
        Search documents using BM25 full-text search with LIKE fallback.
        By default searches only child chunks (for retrieval precision).

        Args:
            kb_id: Knowledge base ID
            query: Natural language search query
            top_k: Number of results to return
            children_only: If True, only search child chunks (default). Set False to search all.
            source_filter: If set, restrict results to chunks from this source file only.

        Returns:
            List of { id, content, source, title, score, parent_id, chunk_level }
        """
        db_path = self._db_path(kb_id)
        if not os.path.exists(db_path):
            return []

        conn = duckdb.connect(db_path, read_only=True)
        try:
            count = conn.execute("SELECT COUNT(*) FROM docs").fetchone()[0]
            if count == 0:
                return []

            # Check if schema has parent_id column
            cols = [row[0] for row in conn.execute("DESCRIBE docs").fetchall()]
            has_parent_col = "parent_id" in cols

            level_filter = ""
            if children_only and has_parent_col:
                level_filter = "AND chunk_level = 'child'"

            # Exclude revision history chunks from search (they pollute BM25 with
            # repeated feature mentions but provide no useful answers)
            rev_filter = "AND title != 'Revision History'" if has_parent_col else ""

            # Source-scoped search: restrict to a specific file
            source_clause = ""
            source_params: list = []
            if source_filter:
                source_clause = "AND source = ?"
                source_params = [source_filter]

            # Try FTS/BM25 first
            results = []
            try:
                conn.execute("LOAD fts;")
                query_sql = f"""
                    SELECT id, source, title, content,
                           fts_main_docs.match_bm25(id, ?) AS score
                           {', parent_id, chunk_level, chunk_number' if has_parent_col else ''}
                    FROM docs
                    WHERE score IS NOT NULL {level_filter} {rev_filter} {source_clause}
                    ORDER BY score DESC
                    LIMIT ?
                """
                rows = conn.execute(query_sql, [query] + source_params + [top_k]).fetchall()
                for row in rows:
                    result = {
                        "id": row[0], "source": row[1], "title": row[2],
                        "content": row[3], "score": row[4],
                    }
                    if has_parent_col:
                        result["parent_id"] = row[5]
                        result["chunk_level"] = row[6]
                        result["chunk_number"] = row[7]
                    results.append(result)
            except Exception:
                pass

            if results:
                return results

            # Fallback: LIKE-based keyword search
            keywords = [w for w in query.split() if len(w) > 2]
            if not keywords:
                return []

            conditions = " OR ".join([f"content ILIKE '%' || ? || '%'" for _ in keywords])
            where = f"({conditions})"
            if children_only and has_parent_col:
                where += " AND chunk_level = 'child'"
            if source_filter:
                where += " AND source = ?"

            fallback_sql = f"""
                SELECT id, source, title, content, 0.1 AS score
                       {', parent_id, chunk_level, chunk_number' if has_parent_col else ''}
                FROM docs
                WHERE {where}
                LIMIT ?
            """
            fallback_params = keywords + ([source_filter] if source_filter else []) + [top_k]
            rows = conn.execute(fallback_sql, fallback_params).fetchall()
            for row in rows:
                result = {
                    "id": row[0], "source": row[1], "title": row[2],
                    "content": row[3], "score": row[4],
                }
                if has_parent_col:
                    result["parent_id"] = row[5]
                    result["chunk_level"] = row[6]
                    result["chunk_number"] = row[7]
                results.append(result)
            return results
        finally:
            conn.close()

    def get_parent(self, kb_id: str, parent_id: str) -> Optional[dict]:
        """Get a parent chunk by ID — returns the full section content."""
        db_path = self._db_path(kb_id)
        if not os.path.exists(db_path):
            return None

        conn = duckdb.connect(db_path, read_only=True)
        try:
            row = conn.execute(
                "SELECT id, source, title, content, chunk_level, chunk_number FROM docs WHERE id = ?",
                [parent_id],
            ).fetchone()
            if row:
                return {
                    "id": row[0], "source": row[1], "title": row[2],
                    "content": row[3], "chunk_level": row[4], "chunk_number": row[5],
                }
            return None
        except Exception:
            return None
        finally:
            conn.close()

    def get_children_of_parent(self, kb_id: str, parent_id: str) -> list[dict]:
        """Get all child chunks belonging to a specific parent."""
        db_path = self._db_path(kb_id)
        if not os.path.exists(db_path):
            return []

        conn = duckdb.connect(db_path, read_only=True)
        try:
            rows = conn.execute(
                "SELECT id, source, title, content, chunk_number FROM docs WHERE parent_id = ? ORDER BY chunk_number",
                [parent_id],
            ).fetchall()
            return [{"id": r[0], "source": r[1], "title": r[2], "content": r[3], "chunk_number": r[4]} for r in rows]
        except Exception:
            return []
        finally:
            conn.close()

    def search_with_parent_expansion(self, kb_id: str, query: str, top_k: int = 10, source_filter: str | None = None) -> list[dict]:
        """
        Search child chunks, then expand results to include full parent section content.
        This is the key pattern: retrieve with precision (children), respond with context (parents).

        Returns:
            List of { id, content, source, title, score, parent_id, parent_content }
        """
        results = self.search(kb_id, query, top_k=top_k, children_only=True, source_filter=source_filter)
        if not results:
            # Fallback to searching all chunks (handles legacy DBs without parent-child)
            return self.search(kb_id, query, top_k=top_k, children_only=False, source_filter=source_filter)

        # Expand with parent content (deduplicate parents)
        seen_parents = set()
        expanded = []
        for r in results:
            parent_id = r.get("parent_id")
            if parent_id and parent_id not in seen_parents:
                parent = self.get_parent(kb_id, parent_id)
                if parent:
                    r["parent_content"] = parent["content"]
                    seen_parents.add(parent_id)
            expanded.append(r)

        return expanded

    def get_info(self, kb_id: str) -> Optional[dict]:
        """Get document count, chunk count, and sample titles."""
        db_path = self._db_path(kb_id)
        if not os.path.exists(db_path):
            return None

        conn = duckdb.connect(db_path, read_only=True)
        try:
            count = conn.execute("SELECT COUNT(*) FROM docs").fetchone()[0]
            titles = conn.execute(
                "SELECT DISTINCT title FROM docs LIMIT 10"
            ).fetchdf()

            # Include parent/child stats if available
            cols = [row[0] for row in conn.execute("DESCRIBE docs").fetchall()]
            stats = {"chunkCount": count, "documentCount": len(titles), "sampleTitles": list(titles["title"])}
            if "chunk_level" in cols:
                parent_count = conn.execute("SELECT COUNT(*) FROM docs WHERE chunk_level = 'parent'").fetchone()[0]
                child_count = conn.execute("SELECT COUNT(*) FROM docs WHERE chunk_level = 'child'").fetchone()[0]
                stats["parentCount"] = parent_count
                stats["childCount"] = child_count
            return stats
        except Exception:
            return None
        finally:
            conn.close()

    def get_chunks_by_source(self, kb_id: str, source: str) -> list[dict]:
        """Get all chunks from a specific source file."""
        db_path = self._db_path(kb_id)
        if not os.path.exists(db_path):
            return []

        conn = duckdb.connect(db_path, read_only=True)
        try:
            cols = [row[0] for row in conn.execute("DESCRIBE docs").fetchall()]
            has_parent_col = "parent_id" in cols

            if has_parent_col:
                results = conn.execute(
                    "SELECT id, source, title, content, parent_id, chunk_level, chunk_number FROM docs WHERE source = ? ORDER BY chunk_number",
                    [source],
                ).fetchall()
                return [{"id": r[0], "source": r[1], "title": r[2], "content": r[3],
                         "parent_id": r[4], "chunk_level": r[5], "chunk_number": r[6]} for r in results]
            else:
                results = conn.execute(
                    "SELECT id, source, title, content FROM docs WHERE source = ?",
                    [source],
                ).fetchdf()
                return results.to_dict("records")
        except Exception:
            return []
        finally:
            conn.close()

    def get_chunk_by_id(self, kb_id: str, chunk_id: str) -> Optional[dict]:
        """Get a single chunk by its exact ID."""
        db_path = self._db_path(kb_id)
        if not os.path.exists(db_path):
            return None

        conn = duckdb.connect(db_path, read_only=True)
        try:
            row = conn.execute(
                "SELECT id, source, title, content FROM docs WHERE id = ?",
                [chunk_id],
            ).fetchone()
            if row:
                return {"id": row[0], "source": row[1], "title": row[2], "content": row[3]}
            return None
        except Exception:
            return None
        finally:
            conn.close()

    def get_adjacent_chunks(self, kb_id: str, chunk_id: str, before: int = 0, after: int = 3) -> list[dict]:
        """Get chunks adjacent to a given chunk ID (legacy support)."""
        import re
        m = re.match(r"^(.+)#chunk-(\d+)$", chunk_id)
        if not m:
            # Try parent-child format
            m = re.match(r"^(.+)#parent-(\d+)$", chunk_id)
            if not m:
                return []

        file_prefix = m.group(1)
        current_idx = int(m.group(2))
        start_idx = max(0, current_idx - before)
        end_idx = current_idx + after

        target_ids = [
            f"{file_prefix}#chunk-{i}"
            for i in range(start_idx, end_idx + 1)
            if i != current_idx
        ]

        if not target_ids:
            return []

        db_path = self._db_path(kb_id)
        if not os.path.exists(db_path):
            return []

        conn = duckdb.connect(db_path, read_only=True)
        try:
            placeholders = ", ".join(["?"] * len(target_ids))
            rows = conn.execute(
                f"SELECT id, source, title, content FROM docs WHERE id IN ({placeholders}) ORDER BY id",
                target_ids,
            ).fetchall()
            results = [{"id": r[0], "source": r[1], "title": r[2], "content": r[3]} for r in rows]
            results.sort(key=lambda x: int(re.search(r"#chunk-(\d+)$", x["id"]).group(1)) if "#chunk-" in x["id"] else 0)
            return results
        except Exception:
            return []
        finally:
            conn.close()

    def delete(self, kb_id: str) -> bool:
        """Delete a document KB's FTS index."""
        db_path = self._db_path(kb_id)
        if os.path.exists(db_path):
            os.remove(db_path)
            return True
        return False


# Singleton instance
doc_search = DocumentSearch()
