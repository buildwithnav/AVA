import pytest
import asyncio
import tempfile
import os

from app.services.doc_search import DocumentSearch


class TestDocSearch:
    def setup_method(self):
        self.tmpdir = tempfile.mkdtemp()
        self.search = DocumentSearch()
        # Override data dir
        from app.config import settings
        self._orig_data_dir = settings.data_dir
        settings.data_dir = self.tmpdir

    def teardown_method(self):
        from app.config import settings
        settings.data_dir = self._orig_data_dir

    def test_ingest_and_search(self):
        chunks = [
            {"id": "doc1#0", "source": "sla.md", "title": "SLA Policy", "content": "P1 tickets must be acknowledged within 15 minutes."},
            {"id": "doc1#1", "source": "sla.md", "title": "SLA Policy", "content": "P2 tickets must be acknowledged within 1 hour."},
            {"id": "doc2#0", "source": "faq.md", "title": "FAQ", "content": "How do I reset my password? Go to settings and click reset."},
        ]

        count = asyncio.get_event_loop().run_until_complete(
            self.search.ingest("test-kb", chunks)
        )
        assert count == 3

        results = self.search.search("test-kb", "P1 ticket response time", top_k=2)
        assert len(results) > 0
        assert "15 minutes" in results[0]["content"]

    def test_search_nonexistent_kb(self):
        results = self.search.search("nonexistent", "anything")
        assert results == []

    def test_ingest_idempotent(self):
        chunks = [
            {"id": "d1", "source": "a.md", "title": "A", "content": "First version"},
        ]
        asyncio.get_event_loop().run_until_complete(
            self.search.ingest("idem-kb", chunks)
        )

        chunks2 = [
            {"id": "d1", "source": "a.md", "title": "A", "content": "Updated version"},
        ]
        asyncio.get_event_loop().run_until_complete(
            self.search.ingest("idem-kb", chunks2)
        )

        results = self.search.search("idem-kb", "version", top_k=5)
        assert len(results) == 1
        assert "Updated" in results[0]["content"]

    def test_get_info(self):
        chunks = [
            {"id": "d1", "source": "a.md", "title": "Doc A", "content": "Content A"},
            {"id": "d2", "source": "b.md", "title": "Doc B", "content": "Content B"},
        ]
        asyncio.get_event_loop().run_until_complete(
            self.search.ingest("info-kb", chunks)
        )

        info = self.search.get_info("info-kb")
        assert info is not None
        assert info["chunkCount"] == 2

    def test_append_preserves_other_sources_and_replaces_same_source(self):
        asyncio.get_event_loop().run_until_complete(
            self.search.ingest(
                "append-kb",
                [
                    {"id": "a1", "source": "a.md", "title": "A", "content": "alpha knowledge"},
                ],
            )
        )

        count = asyncio.get_event_loop().run_until_complete(
            self.search.append(
                "append-kb",
                [
                    {"id": "b1", "source": "b.md", "title": "B", "content": "beta onboarding"},
                ],
            )
        )
        assert count == 1
        assert self.search.get_info("append-kb")["chunkCount"] == 2
        assert self.search.search("append-kb", "alpha", top_k=5)
        assert self.search.search("append-kb", "beta", top_k=5)

        asyncio.get_event_loop().run_until_complete(
            self.search.append(
                "append-kb",
                [
                    {"id": "b1", "source": "b.md", "title": "B", "content": "gamma replacement"},
                ],
            )
        )

        assert self.search.search("append-kb", "beta", top_k=5) == []
        replacement_results = self.search.search("append-kb", "gamma", top_k=5)
        assert len(replacement_results) == 1
        assert replacement_results[0]["source"] == "b.md"
        assert self.search.search("append-kb", "alpha", top_k=5)

    def test_delete_source(self):
        asyncio.get_event_loop().run_until_complete(
            self.search.ingest(
                "source-kb",
                [
                    {"id": "a1", "source": "a.md", "title": "A", "content": "alpha knowledge"},
                    {"id": "b1", "source": "b.md", "title": "B", "content": "beta onboarding"},
                ],
            )
        )

        assert (
            asyncio.get_event_loop().run_until_complete(
                self.search.delete_source("source-kb", "a.md")
            )
            is True
        )
        assert self.search.search("source-kb", "alpha", top_k=5) == []
        assert self.search.search("source-kb", "beta", top_k=5)
        assert (
            asyncio.get_event_loop().run_until_complete(
                self.search.delete_source("source-kb", "missing.md")
            )
            is False
        )
        assert (
            asyncio.get_event_loop().run_until_complete(
                self.search.delete_source("source-kb", "b.md")
            )
            is True
        )
        assert self.search.search("source-kb", "beta", top_k=5) == []

    def test_delete(self):
        chunks = [{"id": "d1", "source": "a.md", "title": "A", "content": "Content"}]
        asyncio.get_event_loop().run_until_complete(
            self.search.ingest("del-kb", chunks)
        )

        assert self.search.delete("del-kb") is True
        assert self.search.search("del-kb", "anything") == []
