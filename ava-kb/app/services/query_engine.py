import csv
import duckdb
import io
import math
import re
import time
from typing import Iterator, Union


def _sanitize_value(v):
    """Sanitize a single value for JSON serialization."""
    if isinstance(v, float) and (math.isnan(v) or math.isinf(v)):
        return None
    return v


def _sanitize_for_json(records: list[dict]) -> list[dict]:
    """Replace NaN/Inf float values with None for JSON-safe serialization."""
    for row in records:
        for k, v in row.items():
            if isinstance(v, float) and (math.isnan(v) or math.isinf(v)):
                row[k] = None
    return records


# Maximum rows returned to the caller (the LLM) from a single query. Bounds token
# cost; the full result set remains available via the uncapped CSV export path
# (execute_query_streaming). When exceeded, the response is flagged as truncated.
ROW_CAP = 250

# Allowlist pattern for view names — alphanumeric, underscore, hyphen
SAFE_NAME = re.compile(r"^[a-zA-Z_][a-zA-Z0-9_\-]*$")

# Blocked SQL patterns — prevent file reads, writes, system access
BLOCKED_PATTERNS = [
    r"\bread_parquet\b",
    r"\bread_csv\b",
    r"\bread_json\b",
    r"\bcopy\b",
    r"\bcreate\s+table\b",
    r"\bdrop\b",
    r"\battach\b",
    r"\bdetach\b",
    r"\binstall\b",
    r"\bload\b",
    r"\bpragma\b",
    r"\bset\b",
    r"\bcall\b",
    r"\binsert\b",
    r"\bupdate\b",
    r"\bdelete\b",
    r"\balter\b",
]
BLOCKED_RE = re.compile("|".join(BLOCKED_PATTERNS), re.IGNORECASE)


def execute_query(dataset_paths: dict[str, str], sql: str) -> dict:
    """
    Execute a sandboxed SQL query over Parquet files.

    Uses Arrow tables for zero-copy registration instead of materializing
    entire datasets into pandas DataFrames, significantly reducing memory usage.

    Args:
        dataset_paths: { "table_name": "/path/to/data.parquet", ... }
        sql: LLM-generated SQL (SELECT only)

    Returns:
        Success: { columns, rows, rowCount, truncated, durationMs }
        Error: { error, message, available_tables, available_columns }
    """
    start = time.time()

    # Validate table names
    for name in dataset_paths:
        if not SAFE_NAME.match(name):
            return {
                "error": "invalid_table_name",
                "message": f"Invalid table name: {name}. Only letters, digits, and underscores allowed.",
            }

    # Block dangerous SQL patterns
    if BLOCKED_RE.search(sql):
        return {
            "error": "blocked_sql",
            "message": "Query contains blocked operations. Only SELECT with standard clauses is allowed.",
        }

    # Ephemeral in-memory connection with external access disabled
    conn = duckdb.connect()
    conn.execute("SET enable_external_access = false")

    try:
        # Load data as Arrow tables (zero-copy) instead of pandas DataFrames.
        # Arrow tables use minimal memory — DuckDB queries them lazily without
        # materializing the entire dataset into Python heap memory.
        for name, path in dataset_paths.items():
            loader = duckdb.connect()
            arrow_table = loader.execute(
                "SELECT * FROM read_parquet(?)", [path]
            ).fetch_arrow_table()
            loader.close()
            # Register Arrow table in the sandboxed connection — zero-copy,
            # DuckDB scans it directly without DataFrame overhead
            conn.register(name, arrow_table)

        # Execute the user query
        result = conn.execute(sql)
        duration_ms = int((time.time() - start) * 1000)

        # Fetch only up to ROW_CAP+1 rows to detect truncation without loading all.
        # The cap bounds what is sent to the LLM (token cost); the full result set is
        # always available to users via the uncapped CSV export path
        # (execute_query_streaming).
        rows = result.fetchmany(ROW_CAP + 1)
        columns = [desc[0] for desc in result.description]

        truncated = len(rows) > ROW_CAP
        if truncated:
            rows = rows[:ROW_CAP]

        # Convert rows to list of dicts with JSON-safe values
        row_dicts = [
            {col: _sanitize_value(val) for col, val in zip(columns, row)}
            for row in rows
        ]

        response = {
            "columns": columns,
            "rows": row_dicts,
            "rowCount": len(row_dicts),
            "truncated": truncated,
            "durationMs": duration_ms,
        }

        # When truncated, report the true total row count so the caller (the LLM)
        # knows the real size. Behavioral guidance (re-run with aggregation, tell the
        # user it was capped) lives in the system prompt / kb_query tool description,
        # not here — the engine only reports indicators.
        if truncated:
            try:
                total = conn.execute(
                    f"SELECT COUNT(*) FROM ({sql}) AS _ava_count"
                ).fetchone()[0]
                response["totalRowCount"] = total
            except duckdb.Error:
                response["totalRowCount"] = None

        return response

    except duckdb.Error as e:
        # Return structured error with available schema info for self-healing
        tables = list(dataset_paths.keys())
        columns = {}
        for name in tables:
            try:
                schema = conn.execute(f'DESCRIBE "{name}"').fetchdf()
                columns[name] = list(schema["column_name"])
            except Exception:
                pass

        return {
            "error": "query_failed",
            "message": str(e),
            "available_tables": tables,
            "available_columns": columns,
        }

    finally:
        conn.close()


class QueryValidationError(Exception):
    """Raised when export SQL fails sandbox validation (bad table name or blocked op)."""

    def __init__(self, error: str, message: str):
        self.error = error
        self.message = message
        super().__init__(message)


def _validate_query(dataset_paths: dict[str, str], sql: str) -> None:
    """Apply the same sandbox checks execute_query uses, raising on violation."""
    for name in dataset_paths:
        if not SAFE_NAME.match(name):
            raise QueryValidationError(
                "invalid_table_name",
                f"Invalid table name: {name}. Only letters, digits, and underscores allowed.",
            )
    if BLOCKED_RE.search(sql):
        raise QueryValidationError(
            "blocked_sql",
            "Query contains blocked operations. Only SELECT with standard clauses is allowed.",
        )


def stream_query_csv(
    dataset_paths: dict[str, str], sql: str, batch_size: int = 2048
) -> Iterator[str]:
    """
    Execute a sandboxed SQL query and yield CSV text in batches — with NO row cap.

    Mirrors execute_query's sandbox (SELECT-only, external access disabled, Arrow
    zero-copy registration) but streams the full result set as CSV rather than
    capping at 1000 rows. Used by the export/download path so users get every row.

    Raises QueryValidationError for invalid table names or blocked SQL so the
    caller can return an HTTP error before streaming begins.
    """
    _validate_query(dataset_paths, sql)

    conn = duckdb.connect()
    conn.execute("SET enable_external_access = false")

    buffer = io.StringIO()
    writer = csv.writer(buffer)

    def _flush() -> str:
        chunk = buffer.getvalue()
        buffer.seek(0)
        buffer.truncate(0)
        return chunk

    try:
        for name, path in dataset_paths.items():
            loader = duckdb.connect()
            arrow_table = loader.execute(
                "SELECT * FROM read_parquet(?)", [path]
            ).fetch_arrow_table()
            loader.close()
            conn.register(name, arrow_table)

        result = conn.execute(sql)
        columns = [desc[0] for desc in result.description]

        # Prepend a UTF-8 BOM so Excel detects the encoding. Without it Excel opens
        # CSVs as legacy Windows-1252 and mangles accented characters (e.g. "Pitié"
        # -> "PitiÃ©"). The BOM is harmless for other UTF-8-aware consumers.
        writer.writerow(columns)
        yield "\ufeff" + _flush()

        while True:
            rows = result.fetchmany(batch_size)
            if not rows:
                break
            for row in rows:
                writer.writerow([_sanitize_value(v) if v is not None else "" for v in row])
            yield _flush()

    finally:
        conn.close()
