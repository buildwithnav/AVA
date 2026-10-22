from contextlib import asynccontextmanager
from fastapi import FastAPI, Depends
from fastapi.middleware.cors import CORSMiddleware

from app.middleware.auth import verify_api_key
from app.routers import health, query, search, upload, ingest, schema, manage
from app.config import settings

import os


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Ensure data directories exist on startup
    for subdir in ["domain", "session", "docs"]:
        os.makedirs(os.path.join(settings.data_dir, subdir), exist_ok=True)
    yield


base_path = os.getenv("BASE_PATH", "")

app = FastAPI(
    title="AVA Knowledge Base Service",
    version="1.0.0",
    root_path=base_path,
    lifespan=lifespan,
)

# Middleware
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Health at root (Docker healthcheck) and under prefix (k8s probes / gateway)
app.include_router(health.router, tags=["health"])
if base_path:
    app.include_router(health.router, prefix=base_path, tags=["health"])

# KB routers under {base_path}/kb — require API key
kb_prefix = f"{base_path}/kb"
app.include_router(query.router, prefix=kb_prefix, tags=["query"], dependencies=[Depends(verify_api_key)])
app.include_router(search.router, prefix=kb_prefix, tags=["search"], dependencies=[Depends(verify_api_key)])
app.include_router(upload.router, prefix=kb_prefix, tags=["upload"], dependencies=[Depends(verify_api_key)])
app.include_router(ingest.router, prefix=kb_prefix, tags=["ingest"], dependencies=[Depends(verify_api_key)])
app.include_router(schema.router, prefix=kb_prefix, tags=["schema"], dependencies=[Depends(verify_api_key)])
app.include_router(manage.router, prefix=kb_prefix, tags=["manage"], dependencies=[Depends(verify_api_key)])
