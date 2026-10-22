import re
from typing import Optional

from app.models import ParentChunk, ChildChunk, DocumentChunks

# Detects revision history/changelog content that should be excluded from search
# These sections have high term frequency for feature names but provide no useful answers
_REVISION_DATE_RE = re.compile(
    r"\d{1,2}[-/]\w{3}[-/]\d{2,4}|\d{4}[-/]\d{2}[-/]\d{2}", re.MULTILINE
)


def _is_revision_history(text: str) -> bool:
    """
    Detect if a parent section is revision history/changelog content.
    These sections contain many date entries with person names and change descriptions.
    They pollute BM25 search because they mention feature names many times.
    """
    # Count date patterns — revision history has many dates
    dates = _REVISION_DATE_RE.findall(text)
    if len(dates) < 3:
        return False

    # Tabular data (from Excel sheets) often has date columns that trigger
    # false positives. Detect by: sheet heading or pipe-separated key-value rows
    # which indicate structured data, not changelogs.
    first_line = text.strip().split("\n")[0].strip().lower()
    if first_line.startswith("## sheet:"):
        return False
    lines = text.strip().split("\n")[:5]
    pipe_lines = sum(1 for line in lines if line.count(" | ") >= 3)
    if pipe_lines >= 2:
        return False

    # Check for revision history heading patterns
    lower = text[:200].lower()
    revision_headings = ["revision history", "change log", "document history",
                         "version history", "change history", "revision log"]
    if any(h in lower for h in revision_headings):
        return True

    # 4+ dates in a parent chunk is a very strong signal for revision history.
    # Real specification sections have 0 dates; only changelogs list many dates.
    if len(dates) >= 4:
        return True

    # 3 dates: check for version-date patterns (e.g., "2.4 30-Jun-2009")
    _version_date_re = re.compile(
        r"\d+\.\d+\s*\d{1,2}[-/]\w{3}[-/]\d{2,4}"
    )
    if len(_version_date_re.findall(text)) >= 2:
        return True

    return False


# Matches lines that look like list items (bullets, dashes, numbered)
_LIST_ITEM_RE = re.compile(
    r"^(\s*)([-•–—*]|\d+[.)]\s|\w[.)]\s)", re.MULTILINE
)



# Matches section headings — numbered sections (e.g., "4.4 TITLE", "4.4.1 Content")
# or markdown headings (## Heading). These should start new chunks to avoid
# being orphaned at the tail of unrelated content.
_HEADING_RE = re.compile(
    r"^(\d+(\.\d+)*\s+\S|#{1,6}\s)", re.MULTILINE
)

# Major section headings that FORCE a parent boundary:
# - Short numbered headings (1-2 levels, < 80 chars) like "4.4 MONITORING VISIT REPORT"
# - Markdown headings (# or ## Section)
# Does NOT force breaks for deep numbered items like "4.4.1 The system shall..."
_MAJOR_HEADING_RE = re.compile(
    r"^(\d+(\.\d+)?)\s+\S", re.MULTILINE
)


def _is_list_item(text: str) -> bool:
    """Check if a paragraph looks like a list/bullet item."""
    return bool(_LIST_ITEM_RE.match(text))


def _is_heading(text: str) -> bool:
    """Check if a paragraph looks like a section heading."""
    return bool(_HEADING_RE.match(text.strip()))


def _is_major_heading(text: str) -> bool:
    """
    Detect major section headings that should force a parent chunk boundary.
    Checks the FIRST LINE of the text — if it's a short numbered heading (1-2 levels)
    or a markdown heading (# or ##), it represents a section TITLE.
    """
    first_line = text.strip().split("\n")[0].strip()
    if re.match(r"^#{1,2}\s", first_line):
        return True
    if len(first_line) < 80 and _MAJOR_HEADING_RE.match(first_line):
        return True
    return False


def _merge_list_paragraphs(paragraphs: list[str]) -> list[str]:
    """
    Merge consecutive list-item paragraphs with their preceding heading.
    This prevents bullet lists from being split across chunks when blank
    lines separate each item (common in markdown, DOCX, and PDF extractions).
    """
    merged = []
    for para in paragraphs:
        if not para.strip():
            continue
        if _is_list_item(para) and merged:
            merged[-1] = merged[-1] + "\n" + para
        else:
            merged.append(para)
    return merged


def _extract_heading(text: str) -> Optional[str]:
    """Extract section heading from the start of a text block."""
    first_line = text.strip().split("\n")[0].strip()
    if _is_heading(first_line) and len(first_line) < 120:
        return first_line
    return None


def _split_into_children(
    parent_content: str,
    parent_id: str,
    source: str,
    title: str,
    child_chunk_size: int = 500,
    child_chunk_overlap: int = 75,
) -> list[ChildChunk]:
    """
    Split a parent section into overlapping child chunks for retrieval precision.
    Children are smaller windows that overlap to ensure no content falls between cracks.
    """
    if len(parent_content) <= child_chunk_size:
        # Parent is small enough to be its own single child
        return [ChildChunk(
            id=f"{parent_id}::child-0",
            source=source,
            title=title,
            content=parent_content,
            chunk_number=0,
            parent_id=parent_id,
        )]

    children = []
    # Split by paragraphs first for cleaner boundaries
    paragraphs = re.split(r"\n\s*\n", parent_content)
    paragraphs = [p.strip() for p in paragraphs if p.strip()]

    current_chunk = ""
    child_idx = 0

    for para in paragraphs:
        if len(current_chunk) + len(para) + 1 <= child_chunk_size:
            current_chunk = f"{current_chunk}\n{para}" if current_chunk else para
        else:
            if current_chunk:
                children.append(ChildChunk(
                    id=f"{parent_id}::child-{child_idx}",
                    source=source,
                    title=title,
                    content=current_chunk.strip(),
                    chunk_number=child_idx,
                    parent_id=parent_id,
                ))
                child_idx += 1
                # Overlap: keep tail of current chunk
                if child_chunk_overlap > 0 and len(current_chunk) > child_chunk_overlap:
                    overlap = current_chunk[-child_chunk_overlap:]
                    current_chunk = f"{overlap}\n{para}"
                else:
                    current_chunk = para
            else:
                # Single paragraph bigger than child_chunk_size — split at line boundaries
                if "\n" in para and len(para) > child_chunk_size:
                    lines = para.split("\n")
                    line_chunk = ""
                    for line in lines:
                        if len(line_chunk) + len(line) + 1 <= child_chunk_size:
                            line_chunk = f"{line_chunk}\n{line}" if line_chunk else line
                        else:
                            if line_chunk:
                                children.append(ChildChunk(
                                    id=f"{parent_id}::child-{child_idx}",
                                    source=source,
                                    title=title,
                                    content=line_chunk.strip(),
                                    chunk_number=child_idx,
                                    parent_id=parent_id,
                                ))
                                child_idx += 1
                                if child_chunk_overlap > 0 and len(line_chunk) > child_chunk_overlap:
                                    overlap = line_chunk[-child_chunk_overlap:]
                                    line_chunk = f"{overlap}\n{line}"
                                else:
                                    line_chunk = line
                            else:
                                line_chunk = line
                    current_chunk = line_chunk
                else:
                    children.append(ChildChunk(
                        id=f"{parent_id}::child-{child_idx}",
                        source=source,
                        title=title,
                        content=para.strip(),
                        chunk_number=child_idx,
                        parent_id=parent_id,
                    ))
                    child_idx += 1
                    current_chunk = ""

    if current_chunk.strip():
        children.append(ChildChunk(
            id=f"{parent_id}::child-{child_idx}",
            source=source,
            title=title,
            content=current_chunk.strip(),
            chunk_number=child_idx,
            parent_id=parent_id,
        ))

    return children


def chunk_document(
    text: str,
    source: str,
    title: Optional[str] = None,
    parent_max_size: int = 2000,
    child_chunk_size: int = 500,
    child_chunk_overlap: int = 75,
) -> DocumentChunks:
    """
    Split a document into parent-child chunk hierarchy.

    Parent chunks = full semantic sections (heading → next heading), up to parent_max_size chars.
    Child chunks = overlapping retrieval windows within each parent (~500 chars).

    The search index uses CHILDREN for retrieval precision, then expands to the
    PARENT for full context when presenting results to the LLM.

    Args:
        text: Full document text
        source: Source identifier (filename)
        title: Optional document title
        parent_max_size: Max size for a parent section before splitting into multiple parents
        child_chunk_size: Target size for child chunks (retrieval windows)
        child_chunk_overlap: Overlap between consecutive child chunks

    Returns:
        DocumentChunks with parents and children lists
    """
    if not text or not text.strip():
        return DocumentChunks(source=source, parents=[], children=[])

    doc_title = title or source

    # Split on paragraph boundaries, merge list items
    paragraphs = re.split(r"\n\s*\n", text.strip())
    paragraphs = _merge_list_paragraphs(paragraphs)

    # Build parent sections by splitting at major headings
    sections: list[str] = []
    current_section = ""

    for para in paragraphs:
        para = para.strip()
        if not para:
            continue

        if _is_major_heading(para) and current_section.strip():
            sections.append(current_section.strip())
            current_section = para
        else:
            current_section = f"{current_section}\n\n{para}" if current_section else para

    if current_section.strip():
        sections.append(current_section.strip())

    # Further split sections that exceed parent_max_size
    final_sections: list[str] = []
    for section in sections:
        if len(section) <= parent_max_size:
            final_sections.append(section)
        else:
            # Split oversized sections at paragraph boundaries first
            sub_paras = re.split(r"\n\s*\n", section)
            sub_section = ""
            for sp in sub_paras:
                # If a single sub-paragraph exceeds parent_max_size, split at line breaks
                if len(sp) > parent_max_size and "\n" in sp:
                    if sub_section:
                        final_sections.append(sub_section.strip())
                        sub_section = ""
                    lines = sp.split("\n")
                    for line in lines:
                        if len(sub_section) + len(line) + 1 > parent_max_size and sub_section:
                            final_sections.append(sub_section.strip())
                            sub_section = line
                        else:
                            sub_section = f"{sub_section}\n{line}" if sub_section else line
                elif len(sub_section) + len(sp) + 2 > parent_max_size and sub_section:
                    final_sections.append(sub_section.strip())
                    sub_section = sp
                else:
                    sub_section = f"{sub_section}\n\n{sp}" if sub_section else sp
            if sub_section.strip():
                final_sections.append(sub_section.strip())

    # Create parent and child chunks
    parents: list[ParentChunk] = []
    children: list[ChildChunk] = []

    for idx, section_text in enumerate(final_sections):
        parent_id = f"{source}#parent-{idx}"
        heading = _extract_heading(section_text)
        section_title = heading if heading else doc_title

        # Detect revision history/changelog sections — still indexed but with
        # a generic title so BM25 doesn't boost them for feature-specific queries
        is_changelog = _is_revision_history(section_text)
        child_title = "Revision History" if is_changelog else section_title

        parent = ParentChunk(
            id=parent_id,
            source=source,
            title=section_title,
            content=section_text,
            chunk_number=idx,
            section_heading=heading,
        )
        parents.append(parent)

        # Generate child chunks within this parent
        section_children = _split_into_children(
            parent_content=section_text,
            parent_id=parent_id,
            source=source,
            title=child_title,
            child_chunk_size=child_chunk_size,
            child_chunk_overlap=child_chunk_overlap,
        )
        children.extend(section_children)

    return DocumentChunks(source=source, parents=parents, children=children)


def chunk_text(
    text: str,
    source: str,
    title: Optional[str] = None,
    chunk_size: int = 500,
    chunk_overlap: int = 50,
) -> list[dict]:
    """
    Legacy flat chunking — splits text into overlapping chunks for FTS indexing.
    Maintained for backward compatibility with dataset/non-document uploads.

    For document uploads, use chunk_document() instead which produces parent-child hierarchy.

    Args:
        text: The full document text
        source: Source identifier (filename, URL, etc.)
        title: Optional document title
        chunk_size: Target chunk size in characters
        chunk_overlap: Overlap between consecutive chunks

    Returns:
        List of { id, source, title, content } dicts
    """
    if not text or not text.strip():
        return []

    paragraphs = re.split(r"\n\s*\n", text.strip())
    paragraphs = _merge_list_paragraphs(paragraphs)

    chunks = []
    current_chunk = ""
    chunk_idx = 0

    for para in paragraphs:
        para = para.strip()
        if not para:
            continue

        if _is_major_heading(para):
            is_top_level = bool(re.match(r"^#{1,2}\s", para.strip())) or \
                (len(para.strip()) < 80 and bool(re.match(r"^\d+(\.\d+)?\s+\S", para.strip())) and para.strip().count('.') <= 1)
            threshold = 0 if is_top_level else chunk_size * 0.25
            if len(current_chunk) > threshold:
                chunks.append({
                    "id": f"{source}#chunk-{chunk_idx}",
                    "source": source,
                    "title": title or source,
                    "content": current_chunk.strip(),
                })
                chunk_idx += 1
                current_chunk = para
                continue

        if len(current_chunk) + len(para) + 1 <= chunk_size:
            current_chunk = f"{current_chunk}\n{para}" if current_chunk else para
        else:
            if current_chunk:
                chunks.append({
                    "id": f"{source}#chunk-{chunk_idx}",
                    "source": source,
                    "title": title or source,
                    "content": current_chunk.strip(),
                })
                chunk_idx += 1
                if chunk_overlap > 0:
                    overlap_text = current_chunk[-chunk_overlap:]
                    current_chunk = f"{overlap_text}\n{para}"
                else:
                    current_chunk = para
            else:
                sentences = re.split(r"(?<=[.!?])\s+", para)
                for sentence in sentences:
                    if len(current_chunk) + len(sentence) + 1 <= chunk_size:
                        current_chunk = (
                            f"{current_chunk} {sentence}" if current_chunk else sentence
                        )
                    else:
                        if current_chunk:
                            chunks.append({
                                "id": f"{source}#chunk-{chunk_idx}",
                                "source": source,
                                "title": title or source,
                                "content": current_chunk.strip(),
                            })
                            chunk_idx += 1
                        current_chunk = sentence

    if current_chunk.strip():
        chunks.append({
            "id": f"{source}#chunk-{chunk_idx}",
            "source": source,
            "title": title or source,
            "content": current_chunk.strip(),
        })

    return chunks


def chunk_qa_pairs(rows: list[dict], source: str) -> list[dict]:
    """
    Convert Q&A rows into chunks. Each row becomes one chunk.

    Expected row format: { "question": "...", "answer": "..." }
    or { "Q": "...", "A": "..." }
    """
    chunks = []
    for i, row in enumerate(rows):
        q = row.get("question") or row.get("Q") or row.get("q", "")
        a = row.get("answer") or row.get("A") or row.get("a", "")
        if q or a:
            content = f"Q: {q}\nA: {a}" if q and a else (q or a)
            chunks.append({
                "id": f"{source}#row-{i}",
                "source": f"{source}#row-{i}",
                "title": q[:100] if q else f"Row {i}",
                "content": content,
            })
    return chunks
