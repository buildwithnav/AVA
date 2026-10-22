"""
Text extraction utilities for PDF, DOCX, and Excel files.
Extracts plain text only — no images, formatting, or layout preservation.
Image references and alt-text artifacts are stripped to prevent LLM hallucination.
"""

import io
import re
from datetime import date, datetime
from pypdf import PdfReader
from docx import Document
from python_calamine import CalamineWorkbook

# Patterns that are unambiguously image references in extracted text.
# Only matches markdown image syntax (![...]) which requires the ! prefix —
# regular brackets like [Table 1], [Figure 3], [see note] are preserved.
_IMAGE_PATTERNS = re.compile(
    r"!\[.*?\]\(.*?\)"    # Full markdown image ![alt](url)
    r"|!\[.*?\]",         # Markdown image syntax ![alt text]
    re.IGNORECASE,
)


def _strip_image_references(text: str) -> str:
    """Remove image alt-text and reference artifacts from extracted text."""
    cleaned = _IMAGE_PATTERNS.sub("", text)
    # Collapse multiple blank lines left behind after stripping
    cleaned = re.sub(r"\n{3,}", "\n\n", cleaned)
    return cleaned


def extract_pdf_text(file_bytes: bytes) -> str:
    """
    Extract all text from a PDF file, stripping image references.

    Args:
        file_bytes: Raw PDF file bytes

    Returns:
        Concatenated text from all pages, separated by double newlines.
    """
    reader = PdfReader(io.BytesIO(file_bytes))
    pages = []
    for page in reader.pages:
        text = page.extract_text()
        if text and text.strip():
            pages.append(text.strip())
    raw_text = "\n\n".join(pages)
    return _strip_image_references(raw_text)


def _heading_level(style_name: str) -> int:
    """
    Detect heading level from a Word style name.
    Returns 1-6 for heading levels, 0 for non-headings.
    Handles "Heading 1", "heading 2", "Title" (treated as level 1), "Subtitle" (level 2).
    """
    lower = style_name.lower()
    if lower == "title":
        return 1
    if lower == "subtitle":
        return 2
    # Match "Heading 1", "Heading 2", etc.
    m = re.match(r"heading\s*(\d+)", lower)
    if m:
        level = int(m.group(1))
        return min(level, 6)
    return 0


def extract_docx_text(file_bytes: bytes) -> str:
    """
    Extract all text from a DOCX file, stripping image references.
    Headings are prefixed with markdown-style markers (## Heading) so the
    chunker can detect section boundaries and force chunk breaks before them.
    List items are joined with single newlines to keep them grouped
    with their preceding heading during chunking.

    Args:
        file_bytes: Raw DOCX file bytes

    Returns:
        Concatenated paragraph text with structure-aware separators.
    """
    doc = Document(io.BytesIO(file_bytes))
    parts = []

    def _process_paragraph(para):
        """Process a single paragraph element and append to parts."""
        text = para.text.strip()
        if not text:
            return

        style_name = (para.style.name or "")

        # Detect heading-styled paragraphs and prefix with markdown markers.
        heading_level = _heading_level(style_name)
        if heading_level > 0:
            prefix = "#" * heading_level + " "
            if not text.startswith("#"):
                text = prefix + text
            parts.append("\n\n" + text if parts else text)
            return

        # Detect list-style paragraphs: keep them grouped with preceding content
        style_lower = style_name.lower()
        is_list_item = (
            "list" in style_lower
            or "bullet" in style_lower
            or "•" in text[:2]
            or text[:2] in ("- ", "– ", "— ")
            or (len(text) > 0 and text[0].isdigit() is False and para.paragraph_format.left_indent and para.paragraph_format.left_indent > 0)
        )

        if is_list_item and parts:
            parts.append("\n" + text)
        else:
            parts.append("\n\n" + text if parts else text)

    def _process_table(table):
        """Extract table content as tab-separated rows, kept attached to preceding heading."""
        rows = []
        for row in table.rows:
            cells = [cell.text.strip() for cell in row.cells]
            # Skip completely empty rows
            if any(cells):
                rows.append("\t".join(cells))
        if rows:
            table_text = "\n".join(rows)
            # Use single newline to keep table attached to its preceding heading/paragraph
            # (chunker splits on \n\n — single \n keeps them in same section)
            parts.append("\n" + table_text if parts else table_text)

    # Iterate body elements in document order to preserve table position context
    from docx.oxml.ns import qn
    body = doc.element.body
    para_iter = iter(doc.paragraphs)
    table_iter = iter(doc.tables)

    for child in body:
        tag = child.tag
        if tag == qn("w:p"):
            try:
                para = next(para_iter)
                _process_paragraph(para)
            except StopIteration:
                pass
        elif tag == qn("w:tbl"):
            try:
                table = next(table_iter)
                _process_table(table)
            except StopIteration:
                pass

    raw_text = "".join(parts)
    return _strip_image_references(raw_text)


def _normalize_xlsx_cell(cell) -> str | None:
    """
    Normalize a python-calamine cell value to a stripped non-empty string,
    or None for empty/missing cells.

    Calamine returns "" (empty string) for blank cells; openpyxl returned None.
    Coerce both to None so downstream emptiness checks behave identically.

    Numerics: calamine returns all numbers as `float` (including whole numbers
    like 30 → 30.0). openpyxl returned `int` for whole-number cells. Coerce
    whole-number floats back to int representation so FTS output reads
    "Age: 30" rather than "Age: 30.0".

    Date handling preserves the openpyxl `data_only=True` output format
    ("YYYY-MM-DD HH:MM:SS") so the FTS index format stays stable across the
    library swap. Calamine yields `datetime.date` for pure-date cells —
    coerce those to `datetime` at midnight so `str()` includes the time part.
    """
    if cell is None:
        return None
    # bool is a subclass of int — handle first so True/False don't get
    # collapsed to "1"/"0" by the numeric coercion below.
    if isinstance(cell, bool):
        return str(cell)
    # datetime is a subclass of date — check datetime first
    if isinstance(cell, datetime):
        return str(cell)
    if isinstance(cell, date):
        return str(datetime(cell.year, cell.month, cell.day))
    if isinstance(cell, float):
        # Render whole-number floats without the trailing ".0" to match
        # openpyxl's int output for whole-number cells. NaN/Inf fall through
        # to default str() (".is_integer()" returns False for both).
        if cell.is_integer():
            return str(int(cell))
        return str(cell)
    text = str(cell).strip()
    return text if text else None


def extract_xlsx_text(file_bytes: bytes) -> str:
    """
    Extract searchable text from all sheets of an Excel workbook.

    Each sheet becomes a markdown-headed section (## Sheet: NAME) so the
    document chunker creates parent boundaries at sheet transitions.
    Rows are formatted as "ColHeader: Value | ColHeader: Value | ..." to keep
    column context attached to each value — critical for search since chunks
    may split across rows.

    Uses python-calamine (Rust-backed) for parsing — drastically lower memory
    and CPU than openpyxl, with support for .xlsx, .xls, .xlsb, .xlsm, .ods.

    Args:
        file_bytes: Raw spreadsheet bytes

    Returns:
        Structured text with section headings per sheet and key-value rows.
    """
    wb = CalamineWorkbook.from_filelike(io.BytesIO(file_bytes))
    parts: list[str] = []

    for sheet_name in wb.sheet_names:
        sheet = wb.get_sheet_by_name(sheet_name)
        raw_rows = sheet.to_python()
        if not raw_rows:
            continue

        # Normalize: calamine '' → None, dates → openpyxl-style strings,
        # everything else → stripped string or None.
        rows: list[list[str | None]] = [
            [_normalize_xlsx_cell(cell) for cell in row] for row in raw_rows
        ]

        # First non-empty row is the header
        headers: list[str] = []
        data_start = 0
        for i, row in enumerate(rows):
            if any(cell is not None for cell in row):
                headers = [cell if cell is not None else "" for cell in row]
                data_start = i + 1
                break

        if not headers or all(h == "" for h in headers):
            continue

        section_lines: list[str] = [f"## Sheet: {sheet_name}"]

        data_rows = rows[data_start:]
        if not data_rows:
            # Header-only sheet — still include for searchability
            section_lines.append(" | ".join(h for h in headers if h))
            parts.append("\n\n".join(section_lines))
            continue

        for row in data_rows:
            # Skip completely empty rows
            if not any(cell is not None for cell in row):
                continue

            pairs: list[str] = []
            for col_idx, cell in enumerate(row):
                if col_idx >= len(headers):
                    break
                header = headers[col_idx]
                if not header:
                    continue
                value = cell if cell is not None else ""
                if value and value.lower() != "none":
                    pairs.append(f"{header}: {value}")

            if pairs:
                section_lines.append(" | ".join(pairs))

        # Use double-newline between rows so the chunker treats each row as its
        # own paragraph → one child chunk per row. This maximizes BM25 relevance
        # since each row is a self-contained retrieval unit.
        parts.append("\n\n".join(section_lines))

    if not parts:
        return ""

    return "\n\n".join(parts)
