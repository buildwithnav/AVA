from fastapi import Request, HTTPException
from starlette.middleware.base import BaseHTTPMiddleware
from app.config import settings


class ApiKeyMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next):
        # Health endpoint is unauthenticated (for Docker health checks)
        if request.url.path == "/health":
            return await call_next(request)

        api_key = request.headers.get("X-KB-Api-Key")
        if api_key != settings.api_key:
            raise HTTPException(status_code=401, detail="Invalid API key")

        return await call_next(request)
