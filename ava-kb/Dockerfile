FROM python:3.11-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app/ ./app/

# Create data directories
RUN mkdir -p /app/data/kb/domain /app/data/kb/session /app/data/kb/docs

EXPOSE 8100

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8100/health')" || exit 1

# Single uvicorn worker. The container has a hard 512Mi limit shared by ALL
# processes, and parsing a spreadsheet is the memory hot-spot (~250MB private
# heap for the ~9MB production file). A per-worker semaphore can't serialize
# parses ACROSS workers, so 2+ workers could each parse at once and stack their
# peaks past 512Mi -> OOM kill. One worker bounds pod memory to baseline + a
# single parse, and cpu=200m means extra workers add little parallelism anyway.
# The uvicorn worker is async, so one worker still serves many concurrent
# requests; the parse is offloaded via asyncio.to_thread. --preload still trims
# cold-start; --max-requests recycles the worker to bound any slow growth.
CMD ["gunicorn", "app.main:app", \
     "-w", "1", \
     "-k", "uvicorn.workers.UvicornWorker", \
     "--preload", \
     "--max-requests", "200", \
     "--max-requests-jitter", "50", \
     "--bind", "0.0.0.0:8100", \
     "--timeout", "120", \
     "--graceful-timeout", "30"]
