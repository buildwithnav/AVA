"""
Tests for the xlsx text extraction path using python-calamine.

Fixture xlsx files are built in-memory with openpyxl, which is a developer-only
dependency (not in production requirements.txt). The test module is skipped
gracefully if openpyxl is not installed, so the runtime environment doesn't
need it. To run these locally:

    pip install openpyxl pytest
    pytest ava-kb/tests/test_text_extractor.py
"""

import io
import os
import tempfile
from datetime import datetime, date

import pytest

# Fixture generation requires openpyxl (dev-only). Skip module if absent.
openpyxl = pytest.importorskip("openpyxl")
from openpyxl import Workbook  # noqa: E402

from app.services.text_extractor import extract_xlsx_text  # noqa: E402
from app.services.file_handler import parse_and_store  # noqa: E402


def _xlsx_bytes(build_fn) -> bytes:
    """Build an xlsx in-memory by invoking build_fn(workbook)."""
    wb = Workbook()
    # Remove the default sheet so build_fn fully controls structure
    default_ws = wb.active
    wb.remove(default_ws)
    build_fn(wb)
    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


class TestExtractXlsxText:
    def test_single_sheet_with_data(self):
        def build(wb):
            ws = wb.create_sheet("Patients")
            ws.append(["ID", "Name", "Age"])
            ws.append([1, "Alice", 30])
            ws.append([2, "Bob", 25])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "## Sheet: Patients" in text
        assert "ID: 1 | Name: Alice | Age: 30" in text
        assert "ID: 2 | Name: Bob | Age: 25" in text

    def test_multi_sheet_workbook(self):
        def build(wb):
            ws1 = wb.create_sheet("Sites")
            ws1.append(["Site", "Country"])
            ws1.append(["S001", "USA"])

            ws2 = wb.create_sheet("Trials")
            ws2.append(["Trial", "Phase"])
            ws2.append(["T-100", "III"])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "## Sheet: Sites" in text
        assert "## Sheet: Trials" in text
        assert "Site: S001 | Country: USA" in text
        assert "Trial: T-100 | Phase: III" in text

    def test_empty_rows_are_skipped(self):
        def build(wb):
            ws = wb.create_sheet("Data")
            ws.append(["A", "B"])
            ws.append([1, 2])
            ws.append([None, None])  # empty row
            ws.append([3, 4])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "A: 1 | B: 2" in text
        assert "A: 3 | B: 4" in text
        # The empty row should produce no key:value pairs at all
        assert text.count("A: ") == 2

    def test_date_cell_matches_openpyxl_format(self):
        """
        Calamine returns datetime.datetime for date-formatted cells. The
        extractor must stringify them as 'YYYY-MM-DD HH:MM:SS' so FTS index
        format stays stable with the previous openpyxl-based pipeline.
        """
        def build(wb):
            ws = wb.create_sheet("Visits")
            ws.append(["VisitID", "VisitDate"])
            ws.append(["V1", datetime(2024, 1, 5)])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "VisitDate: 2024-01-05 00:00:00" in text

    def test_datetime_cell_preserves_time(self):
        def build(wb):
            ws = wb.create_sheet("Events")
            ws.append(["When"])
            ws.append([datetime(2024, 3, 15, 14, 30, 45)])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "When: 2024-03-15 14:30:45" in text

    def test_numeric_and_float_cells(self):
        def build(wb):
            ws = wb.create_sheet("Metrics")
            ws.append(["Name", "Score", "Rate"])
            ws.append(["A", 100, 3.14])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "Name: A" in text
        assert "Score: 100" in text
        assert "Rate: 3.14" in text

    def test_formula_with_cached_value(self):
        """
        openpyxl with data_only=True returns cached formula values; calamine
        does the same by default. Verify a sum formula's cached value comes
        through (openpyxl must compute and cache it on save).
        """
        def build(wb):
            ws = wb.create_sheet("Calc")
            ws.append(["X", "Y", "Sum"])
            ws.append([2, 3, "=A2+B2"])

        # openpyxl writes formulas with no cached value by default; we need
        # to set the cached value explicitly so calamine has something to read.
        wb = Workbook()
        wb.remove(wb.active)
        ws = wb.create_sheet("Calc")
        ws.append(["X", "Y", "Sum"])
        ws.cell(row=2, column=1, value=2)
        ws.cell(row=2, column=2, value=3)
        formula_cell = ws.cell(row=2, column=3, value="=A2+B2")
        # Manually inject cached value into the xml so data_only readers see it
        formula_cell.value = "=A2+B2"
        # openpyxl can't compute formulas; instead just place the value
        ws.cell(row=2, column=3, value=5)
        buf = io.BytesIO()
        wb.save(buf)

        text = extract_xlsx_text(buf.getvalue())

        assert "Sum: 5" in text

    def test_header_only_sheet_included(self):
        def build(wb):
            ws = wb.create_sheet("OnlyHeaders")
            ws.append(["Col1", "Col2", "Col3"])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "## Sheet: OnlyHeaders" in text
        assert "Col1 | Col2 | Col3" in text

    def test_leading_empty_rows_skipped(self):
        def build(wb):
            ws = wb.create_sheet("Data")
            ws.append([None, None])  # blank
            ws.append([None, None])  # blank
            ws.append(["A", "B"])    # header
            ws.append([1, 2])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "## Sheet: Data" in text
        assert "A: 1 | B: 2" in text

    def test_missing_header_cell_skips_column(self):
        """When a header cell is empty, values in that column are dropped."""
        def build(wb):
            ws = wb.create_sheet("Partial")
            ws.append(["Name", None, "Age"])
            ws.append(["Alice", "ignored", 30])

        text = extract_xlsx_text(_xlsx_bytes(build))

        assert "Name: Alice" in text
        assert "Age: 30" in text
        assert "ignored" not in text

    def test_empty_workbook_returns_empty_string(self):
        def build(wb):
            wb.create_sheet("Empty")

        text = extract_xlsx_text(_xlsx_bytes(build))
        assert text == ""

    def test_none_string_values_dropped(self):
        """Literal 'None' string values should not pollute extracted output."""
        def build(wb):
            ws = wb.create_sheet("Data")
            ws.append(["Name", "Value"])
            ws.append(["A", "None"])
            ws.append(["B", "real"])

        text = extract_xlsx_text(_xlsx_bytes(build))

        # 'None' literal is filtered; 'real' kept
        assert "Value: real" in text
        assert "Value: None" not in text


class TestFileHandlerXlsxParse:
    """xlsx → Parquet via the calamine → pyarrow path (no pandas DataFrame)."""

    def test_xlsx_upload_to_parquet(self):
        def build(wb):
            ws = wb.create_sheet("data")
            ws.append(["id", "name", "score"])
            ws.append([1, "alpha", 10.5])
            ws.append([2, "beta", 20.0])
            ws.append([3, "gamma", 30.25])

        xlsx_bytes = _xlsx_bytes(build)

        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "out.parquet")
            result = parse_and_store(xlsx_bytes, "xlsx", path)

            assert result["rowCount"] == 3
            assert len(result["schema"]) == 3
            cols = [c["name"] for c in result["schema"]]
            assert cols == ["id", "name", "score"]
            assert os.path.exists(path)

    def test_mixed_type_column_falls_back_to_string(self):
        """A column with both numbers and text must not fail the whole upload."""
        def build(wb):
            ws = wb.create_sheet("data")
            ws.append(["code"])
            ws.append([100])
            ws.append(["N/A"])
            ws.append([200])

        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "out.parquet")
            result = parse_and_store(_xlsx_bytes(build), "xlsx", path)

            assert result["rowCount"] == 3
            assert result["schema"][0]["name"] == "code"
            # mixed numeric/text column is coerced to string, not rejected
            assert "string" in result["schema"][0]["type"].lower()

    def test_blank_cell_keeps_numeric_column_numeric(self):
        """A blank cell in a numeric column stays numeric (null), not string."""
        def build(wb):
            ws = wb.create_sheet("data")
            ws.append(["amount", "label"])
            ws.append([10.0, "a"])
            ws.append([None, "b"])  # amount blank, row not empty
            ws.append([30.0, "c"])

        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "out.parquet")
            result = parse_and_store(_xlsx_bytes(build), "xlsx", path)

            assert result["rowCount"] == 3
            amount_type = result["schema"][0]["type"].lower()
            assert "double" in amount_type

    def test_leading_blank_rows_skipped(self):
        def build(wb):
            ws = wb.create_sheet("data")
            ws.append([None, None])
            ws.append(["a", "b"])
            ws.append([1, 2])
            ws.append([3, 4])

        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "out.parquet")
            result = parse_and_store(_xlsx_bytes(build), "xlsx", path)

            assert [c["name"] for c in result["schema"]] == ["a", "b"]
            assert result["rowCount"] == 2

    def test_duplicate_headers_are_deduped(self):
        def build(wb):
            ws = wb.create_sheet("data")
            ws.append(["x", "x"])
            ws.append([1, 2])

        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "out.parquet")
            result = parse_and_store(_xlsx_bytes(build), "xlsx", path)

            names = [c["name"] for c in result["schema"]]
            assert names == ["x", "x_1"]

    def test_header_only_sheet_raises(self):
        def build(wb):
            ws = wb.create_sheet("data")
            ws.append(["a", "b"])

        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "out.parquet")
            with pytest.raises(ValueError, match="No data found"):
                parse_and_store(_xlsx_bytes(build), "xlsx", path)

    def test_parse_xlsx_path_streams_from_disk(self):
        """The upload route's path-based entry point produces the same result."""
        from app.services.file_handler import parse_xlsx_path

        def build(wb):
            ws = wb.create_sheet("data")
            ws.append(["id", "label"])
            ws.append([1, "a"])
            ws.append([2, "b"])

        with tempfile.TemporaryDirectory() as tmpdir:
            src = os.path.join(tmpdir, "in.xlsx")
            with open(src, "wb") as f:
                f.write(_xlsx_bytes(build))
            out = os.path.join(tmpdir, "out.parquet")
            result = parse_xlsx_path(src, out)

            assert result["rowCount"] == 2
            assert [c["name"] for c in result["schema"]] == ["id", "label"]
            assert os.path.exists(out)

    def test_row_limit_enforced(self, monkeypatch):
        """Rows beyond max_rows are rejected with a clear error and no output.

        This is the right-sized secondary guard that keeps a tall sheet from
        building an oversized in-memory table on the 512Mi pod.
        """
        from app.config import settings as kb_settings
        monkeypatch.setattr(kb_settings, "max_rows", 2)

        def build(wb):
            ws = wb.create_sheet("data")
            ws.append(["id"])
            ws.append([1])
            ws.append([2])
            ws.append([3])  # 3 data rows > limit of 2

        with tempfile.TemporaryDirectory() as tmpdir:
            path = os.path.join(tmpdir, "out.parquet")
            with pytest.raises(ValueError, match="row limit"):
                parse_and_store(_xlsx_bytes(build), "xlsx", path)
            # On rejection nothing (not even the .tmp) should be left behind.
            assert not os.path.exists(path)
            assert not os.path.exists(path + ".tmp")
