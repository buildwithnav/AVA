from pydantic import BaseModel, Field
from typing import Optional
from enum import Enum


# === Enums ===

class KBType(str, Enum):
    dataset = "dataset"
    document = "document"


class KBScope(str, Enum):
    domain = "domain"
    session = "session"


class DataFormat(str, Enum):
    json = "json"
    csv = "csv"
    xlsx = "xlsx"


# === Request Models ===

class IngestRequest(BaseModel):
    kb_id: str = Field(..., alias="kbId")
    scope: KBScope
    session_id: Optional[str] = Field(None, alias="sessionId")
    domain_id: Optional[str] = Field(None, alias="domainId")
    name: str
    format: DataFormat
    data: Optional[list[dict]] = None  # For JSON format inline data

    class Config:
        populate_by_name = True


class QueryRequest(BaseModel):
    dataset_ids: list[str] = Field(..., alias="datasetIds")
    sql: str
    dataset_paths: Optional[dict[str, str]] = Field(None, alias="datasetPaths")

    class Config:
        populate_by_name = True


class SearchRequest(BaseModel):
    knowledge_base_id: str = Field(..., alias="knowledgeBaseId")
    query: str
    top_k: int = Field(5, alias="topK")
    source: Optional[str] = None

    class Config:
        populate_by_name = True


class SchemaRequest(BaseModel):
    knowledge_base_id: str = Field(..., alias="knowledgeBaseId")
    path: str
    type: KBType

    class Config:
        populate_by_name = True


class RefreshRequest(BaseModel):
    api_endpoint: Optional[str] = Field(None, alias="apiEndpoint")
    data: Optional[list[dict]] = None
    format: Optional[DataFormat] = None


# === Response Models ===

class ColumnSchema(BaseModel):
    name: str
    type: str


class IngestResponse(BaseModel):
    kb_id: str = Field(..., alias="kbId")
    type: KBType
    schema_: Optional[list[ColumnSchema]] = Field(None, alias="schema")
    row_count: Optional[int] = Field(None, alias="rowCount")
    chunk_count: Optional[int] = Field(None, alias="chunkCount")
    sample_rows: Optional[list[dict]] = Field(None, alias="sampleRows")

    class Config:
        populate_by_name = True


class QueryResponse(BaseModel):
    columns: list[str]
    rows: list[dict]
    row_count: int = Field(..., alias="rowCount")
    truncated: bool = False
    duration_ms: Optional[int] = Field(None, alias="durationMs")

    class Config:
        populate_by_name = True


class QueryErrorResponse(BaseModel):
    error: str
    message: str
    available_tables: Optional[list[str]] = Field(None, alias="availableTables")
    available_columns: Optional[dict[str, list[str]]] = Field(None, alias="availableColumns")

    class Config:
        populate_by_name = True


class SearchChunk(BaseModel):
    content: str
    source: str
    score: float
    title: Optional[str] = None


class SearchResponse(BaseModel):
    chunks: list[SearchChunk]


class DatasetSchemaResponse(BaseModel):
    kb_id: str = Field(..., alias="kbId")
    type: str = "dataset"
    columns: list[ColumnSchema]
    row_count: int = Field(..., alias="rowCount")
    sample_rows: list[dict] = Field(default_factory=list, alias="sampleRows")

    class Config:
        populate_by_name = True


class DocumentSchemaResponse(BaseModel):
    kb_id: str = Field(..., alias="kbId")
    type: str = "document"
    document_count: int = Field(..., alias="documentCount")
    chunk_count: int = Field(..., alias="chunkCount")
    sample_titles: list[str] = Field(default_factory=list, alias="sampleTitles")

    class Config:
        populate_by_name = True


class HealthResponse(BaseModel):
    status: str = "ok"


class DeleteResponse(BaseModel):
    deleted: bool = True


class RefreshResponse(BaseModel):
    kb_id: str = Field(..., alias="kbId")
    updated: bool = True
    row_count: Optional[int] = Field(None, alias="rowCount")

    class Config:
        populate_by_name = True
