from fastapi import Header, HTTPException

from app.config import settings


async def verify_api_key(x_kb_api_key: str = Header(alias="X-KB-Api-Key")) -> None:
    """FastAPI dependency that validates the API key header."""
    if x_kb_api_key != settings.api_key:
        raise HTTPException(status_code=401, detail="Invalid API key")
