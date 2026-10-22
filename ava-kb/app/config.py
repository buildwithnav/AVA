from pydantic_settings import BaseSettings
import os


class Settings(BaseSettings):
    api_key: str = os.getenv("KB_API_KEY", "change-me-in-production")
    data_dir: str = os.getenv("DATA_DIR", "data/kb")
    # PRIMARY memory guard. The calamine->pyarrow->Parquet parse materializes the
    # sheet before writing, costing ~28MB of RSS per MB of XLSX on top of a
    # ~120MB import baseline. On the fixed 512Mi pod a single ~9MB file peaks at
    # ~365MB; 12MB keeps the worst case near ~460MB with headroom. Raising this
    # (MAX_FILE_SIZE_MB) without raising pod memory risks OOM during parse.
    max_file_size_mb: int = int(os.getenv("MAX_FILE_SIZE_MB", "12"))
    # SECONDARY sanity guard against pathologically tall sheets. The size limit
    # above is the binding memory constraint for typical widths; this just caps
    # absurd row counts. NOTE: the query path (query_engine) still materializes a
    # dataset per query, so very large KBs may need more pod memory at query time.
    max_rows: int = int(os.getenv("MAX_ROWS", "250000"))
    chunk_size: int = int(os.getenv("CHUNK_SIZE", "1000"))
    chunk_overlap: int = int(os.getenv("CHUNK_OVERLAP", "125"))

    class Config:
        env_prefix = ""


settings = Settings()
