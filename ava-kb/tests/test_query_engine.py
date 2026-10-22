import pytest

from app.services.query_engine import execute_query


class TestQueryEngine:
    def setup_method(self):
        """Create a test Parquet file."""
        import pandas as pd
        import duckdb
        import tempfile
        import os

        self.tmpdir = tempfile.mkdtemp()
        self.parquet_path = os.path.join(self.tmpdir, "test.parquet")

        df = pd.DataFrame({
            "StudyId": ["NCT001", "NCT002", "NCT003", "NCT004"],
            "Name": ["Alpha", "Beta", "Gamma", "Delta"],
            "Enrollment": [500, 100, 50, 300],
            "Status": ["Active", "Completed", "Active", "Paused"],
        })
        conn = duckdb.connect()
        conn.execute(f"COPY (SELECT * FROM df) TO '{self.parquet_path}' (FORMAT PARQUET)")
        conn.close()

    def test_basic_select(self):
        result = execute_query(
            {"trial_report": self.parquet_path},
            "SELECT * FROM trial_report",
        )
        assert "error" not in result
        assert result["rowCount"] == 4
        assert len(result["columns"]) == 4

    def test_filtered_query(self):
        result = execute_query(
            {"trial_report": self.parquet_path},
            "SELECT StudyId, Enrollment FROM trial_report WHERE Enrollment > 200",
        )
        assert "error" not in result
        assert result["rowCount"] == 2

    def test_aggregation(self):
        result = execute_query(
            {"trial_report": self.parquet_path},
            "SELECT Status, AVG(Enrollment) as avg_enrollment FROM trial_report GROUP BY Status",
        )
        assert "error" not in result
        assert result["rowCount"] == 3  # Active, Completed, Paused

    def test_bad_column_returns_error(self):
        result = execute_query(
            {"trial_report": self.parquet_path},
            "SELECT Enrollmnt FROM trial_report",
        )
        assert result["error"] == "query_failed"
        assert "available_columns" in result

    def test_blocked_sql(self):
        result = execute_query(
            {"trial_report": self.parquet_path},
            "DELETE FROM trial_report",
        )
        assert result["error"] == "blocked_sql"

    def test_read_parquet_blocked(self):
        result = execute_query(
            {"trial_report": self.parquet_path},
            "SELECT * FROM read_parquet('/etc/passwd')",
        )
        assert result["error"] == "blocked_sql"

    def test_invalid_table_name(self):
        result = execute_query(
            {"drop; --": self.parquet_path},
            "SELECT 1",
        )
        assert result["error"] == "invalid_table_name"

    def test_result_truncation(self):
        # Create a large dataset
        import pandas as pd
        import duckdb
        import os

        large_path = os.path.join(self.tmpdir, "large.parquet")
        df = pd.DataFrame({"id": range(2000), "value": range(2000)})
        conn = duckdb.connect()
        conn.execute(f"COPY (SELECT * FROM df) TO '{large_path}' (FORMAT PARQUET)")
        conn.close()

        result = execute_query(
            {"big_table": large_path},
            "SELECT * FROM big_table",
        )
        assert result["rowCount"] == 2000
        assert len(result["rows"]) == 1000
        assert result["truncated"] is True
