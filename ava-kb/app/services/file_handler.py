import pandas as pd
import duckdb
import pyarrow as pa
import pyarrow.parquet as pq
import io
import json
import os
import math
from typing import Union

from python_calamine import CalamineWorkbook

from app.config import settings


def _sanitize_for_json(records: list[dict]) -> list[dict]:
    """Replace NaN/Inf float values with None for JSON-safe serialization."""
    for row in records:
        for k, v in row.items():
            if isinstance(v, float) and (math.isnan(v) or math.isinf(v)):
                row[k] = None
    return records


def _norm_cell(v):
    """Normalize a calamine cell: empty string -> None, keep native types."""
    if v is None:
        return None
    if isinstance(v, str):
        return v if v != "" else None
    return v


def _to_text(v):
    """Stringify a cell for the mixed-type fallback (None stays None)."""
    if v is None:
        return None
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(v)


def _build_array(values: list) -> pa.Array:
    """
    Build a typed pyarrow array from a column of calamine values.

    pyarrow infers the type from the whole column (numbers -> double, dates ->
    timestamp, etc.). If a column has genuinely mixed types that can't be
    unified, fall back to reading the entire column as strings rather than
    failing the whole upload.
    """
    try:
        return pa.array(values)
    except (pa.ArrowInvalid, pa.ArrowTypeError, pa.ArrowNotImplementedError, OverflowError):
        return pa.array([_to_text(v) for v in values], type=pa.string())


def _make_headers(header_row: list) -> list[str]:
    """Build unique, non-empty column names from the header row."""
    headers: list[str] = []
    seen: dict[str, int] = {}
    for i, cell in enumerate(header_row):
        name = str(cell).strip() if cell is not None else ""
        if not name:
            name = f"column_{i}"
        if name in seen:
            seen[name] += 1
            name = f"{name}_{seen[name]}"
        else:
            seen[name] = 0
        headers.append(name)
    return headers


def _xlsx_to_parquet(wb: CalamineWorkbook, output_path: str) -> dict:
    """
    Convert an open calamine workbook (first sheet) to Parquet without ever
    materializing a pandas DataFrame.

    calamine parses the sheet in Rust; we then build one typed pyarrow column
    at a time and write the table to Parquet. This keeps Python heap usage far
    below the previous ``pd.read_excel`` path, which built an object-dtype
    DataFrame and then copied it again through DuckDB — the cause of OOM kills
    on large files in memory-capped pods. The sheet is still materialized in
    memory (via calamine ``to_python``), so peak memory scales with file size;
    the upload route bounds that with ``max_file_size_mb``.
    """
    if not wb.sheet_names:
        raise ValueError("No data found in file")

    sheet = wb.get_sheet_by_name(wb.sheet_names[0])
    rows = sheet.to_python()

    # First non-empty row is the header.
    header_idx = None
    for i, row in enumerate(rows):
        if any(_norm_cell(c) is not None for c in row):
            header_idx = i
            break
    if header_idx is None:
        raise ValueError("No data found in file")

    headers = _make_headers(rows[header_idx])
    # Keep only non-empty data rows after the header.
    data_rows = [
        r for r in rows[header_idx + 1:]
        if any(_norm_cell(c) is not None for c in r)
    ]

    if not data_rows:
        raise ValueError("No data found in file")
    if len(data_rows) > settings.max_rows:
        raise ValueError(
            f"Data exceeds {settings.max_rows} row limit ({len(data_rows)} rows)"
        )

    # Build columns one at a time to keep peak memory low.
    ncols = len(headers)
    arrays: list[pa.Array] = []
    for ci in range(ncols):
        col = [_norm_cell(r[ci]) if ci < len(r) else None for r in data_rows]
        arrays.append(_build_array(col))
        del col

    table = pa.Table.from_arrays(arrays, names=headers)
    del arrays, rows, data_rows

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    tmp_path = output_path + ".tmp"
    pq.write_table(table, tmp_path)
    os.replace(tmp_path, output_path)

    schema = [{"name": f.name, "type": str(f.type)} for f in table.schema]
    sample_rows = _sanitize_for_json(table.slice(0, 3).to_pylist())

    return {
        "schema": schema,
        "rowCount": table.num_rows,
        "sampleRows": sample_rows,
    }


def parse_and_store(
    file_bytes: Union[bytes, list[dict]],
    format: str,
    output_path: str,
) -> dict:
    """
    Parse CSV/Excel/JSON data into a DataFrame and write to Parquet.
    Shared by /kb/upload and /kb/ingest.

    Args:
        file_bytes: Raw bytes (csv/xlsx) or list of dicts (json inline)
        format: "json", "csv", or "xlsx"
        output_path: Where to write the Parquet file

    Returns:
        { schema: [...], rowCount: int, sampleRows: [...] }
    """
    # Size check for raw bytes
    if isinstance(file_bytes, bytes):
        size_mb = len(file_bytes) / (1024 * 1024)
        if size_mb > settings.max_file_size_mb:
            raise ValueError(
                f"File exceeds {settings.max_file_size_mb}MB limit ({size_mb:.1f}MB)"
            )

    # Parse into DataFrame
    if format == "json":
        if isinstance(file_bytes, list):
            df = pd.json_normalize(file_bytes)
        elif isinstance(file_bytes, bytes):
            data = json.loads(file_bytes)
            if isinstance(data, list):
                df = pd.json_normalize(data)
            elif isinstance(data, dict):
                # Single object — wrap in list
                df = pd.json_normalize([data])
            else:
                raise ValueError(f"JSON must be an array or object, got {type(data).__name__}")
        else:
            raise ValueError(f"Unsupported JSON input type: {type(file_bytes).__name__}")
    elif format == "csv":
        buf = io.BytesIO(file_bytes) if isinstance(file_bytes, bytes) else file_bytes
        try:
            df = pd.read_csv(buf)
        except pd.errors.EmptyDataError:
            raise ValueError("No data found in file")
    elif format == "xlsx":
        src = io.BytesIO(file_bytes) if isinstance(file_bytes, bytes) else file_bytes
        # calamine -> pyarrow -> Parquet (no pandas DataFrame). Returns directly.
        return _xlsx_to_parquet(CalamineWorkbook.from_filelike(src), output_path)
    else:
        raise ValueError(f"Unsupported format: {format}")

    # Validate
    if len(df) == 0:
        raise ValueError("No data found in file")

    if len(df) > settings.max_rows:
        raise ValueError(
            f"Data exceeds {settings.max_rows} row limit ({len(df)} rows)"
        )

    # Ensure output directory exists
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    # Write Parquet via DuckDB (efficient columnar write)
    # Use atomic write: write to temp file then rename to prevent corruption
    # if concurrent reads happen during write
    tmp_path = output_path + ".tmp"
    conn = duckdb.connect()
    try:
        conn.execute(
            f"COPY (SELECT * FROM df) TO '{tmp_path}' (FORMAT PARQUET)"
        )
    finally:
        conn.close()
    os.replace(tmp_path, output_path)

    # Build schema info
    schema = [
        {"name": str(col), "type": str(dtype)}
        for col, dtype in zip(df.columns, df.dtypes)
    ]
    sample_rows = _sanitize_for_json(df.head(3).to_dict("records"))

    return {
        "schema": schema,
        "rowCount": len(df),
        "sampleRows": sample_rows,
    }


def parse_xlsx_path(input_path: str, output_path: str) -> dict:
    """
    Parse an XLSX/XLS file from a filesystem path into Parquet.

    Used by the upload route, which streams the request body to a temp file so
    the full payload is never held in memory. calamine reads the file directly
    (via the file object), so only the parsed columns occupy the Python heap.
    """
    with open(input_path, "rb") as f:
        return _xlsx_to_parquet(CalamineWorkbook.from_filelike(f), output_path)


def get_parquet_schema(parquet_path: str) -> dict:
    """Read schema and sample rows from an existing Parquet file."""
    conn = duckdb.connect()
    try:
        # Get schema
        schema_df = conn.execute(
            f"DESCRIBE SELECT * FROM read_parquet(?)", [parquet_path]
        ).fetchdf()
        columns = [
            {"name": row["column_name"], "type": row["column_type"]}
            for _, row in schema_df.iterrows()
        ]

        # Get row count
        count = conn.execute(
            f"SELECT COUNT(*) as cnt FROM read_parquet(?)", [parquet_path]
        ).fetchone()[0]

        # Get sample rows
        sample_df = conn.execute(
            f"SELECT * FROM read_parquet(?) LIMIT 3", [parquet_path]
        ).fetchdf()
        sample_rows = _sanitize_for_json(sample_df.to_dict("records"))

        return {
            "columns": columns,
            "rowCount": count,
            "sampleRows": sample_rows,
        }
    finally:
        conn.close()
