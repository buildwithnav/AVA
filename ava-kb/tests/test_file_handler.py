import pytest
import os
import tempfile

from app.services.file_handler import parse_and_store, get_parquet_schema


class TestFileHandler:
    def test_csv_parse_and_store(self):
        csv_data = b"Name,Age,City\nAlice,30,NYC\nBob,25,LA\nCharlie,35,SF"
        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "test.parquet")
            result = parse_and_store(csv_data, "csv", path)

            assert result["rowCount"] == 3
            assert len(result["schema"]) == 3
            assert result["schema"][0]["name"] == "Name"
            assert os.path.exists(path)

    def test_json_parse_and_store(self):
        data = [
            {"id": 1, "name": "Trial A", "enrollment": 500},
            {"id": 2, "name": "Trial B", "enrollment": 300},
        ]
        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "test.parquet")
            result = parse_and_store(data, "json", path)

            assert result["rowCount"] == 2
            assert len(result["schema"]) == 3

    def test_nested_json_flattening(self):
        data = [
            {"id": 1, "details": {"name": "Trial A", "phase": "III"}},
            {"id": 2, "details": {"name": "Trial B", "phase": "II"}},
        ]
        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "test.parquet")
            result = parse_and_store(data, "json", path)

            assert result["rowCount"] == 2
            col_names = [c["name"] for c in result["schema"]]
            assert "details.name" in col_names
            assert "details.phase" in col_names

    def test_empty_data_raises(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "test.parquet")
            with pytest.raises(ValueError, match="No data found"):
                parse_and_store(b"", "csv", path)

    def test_json_bytes_parse(self):
        import json
        data = json.dumps([{"a": 1}, {"a": 2}]).encode()
        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "test.parquet")
            result = parse_and_store(data, "json", path)
            assert result["rowCount"] == 2

    def test_get_parquet_schema(self):
        data = [{"x": 10, "y": "hello"}, {"x": 20, "y": "world"}]
        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "test.parquet")
            parse_and_store(data, "json", path)

            schema = get_parquet_schema(path)
            assert schema["rowCount"] == 2
            assert len(schema["columns"]) == 2
            assert len(schema["sampleRows"]) == 2
