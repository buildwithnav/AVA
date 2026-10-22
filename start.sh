#!/usr/bin/env bash
set -euo pipefail

# ── Colors ────────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; MAGENTA='\033[0;35m'; BLUE='\033[0;34m'
DARKCYAN='\033[0;36m'; DARKYELLOW='\033[0;33m'
BOLD='\033[1m'; RESET='\033[0m'

ROOT="$(cd "$(dirname "$0")" && pwd)"
PIDS=()
# Holds VAR=VALUE pairs whose names contain hyphens or other chars that bash
# cannot export natively. Passed to child processes via `env` in each launcher.
_ENV_EXTRAS=()

# ── Load .env file into current shell environment ─────────────────────────────
# Uses printf '%s\n' ... | env -S to bypass the shell's identifier restrictions.
# This handles variable names that contain hyphens (e.g. AVA__Services__x-y__Z),
# which bash/zsh reject in `export` but .NET reads fine from the process environment.
load_env_file() {
  local file="$1"
  if [ -f "$file" ]; then
    while IFS= read -r line || [ -n "$line" ]; do
      # Trim leading/trailing whitespace; skip blank lines and comments
      line="${line#"${line%%[![:space:]]*}"}"
      line="${line%"${line##*[![:space:]]}"}"
      [[ -z "$line" || "$line" == \#* ]] && continue

      # Split on the first '=' only
      local name="${line%%=*}"
      local value="${line#*=}"

      # Validate: name must start with letter/underscore and contain only
      # word chars OR hyphens. Export normally if valid shell identifier;
      # otherwise inject directly into the child-process environment using
      # a wrapper so dotnet/python subprocesses still inherit the value.
      if [[ "$name" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
        export "$name=$value"
      else
        # Name contains chars invalid for shell identifiers (e.g. hyphens).
        # Store in _ENV_EXTRAS array; start_dotnet/start_python prepend these
        # via `env` when launching child processes.
        _ENV_EXTRAS+=("$name=$value")
      fi
    done < "$file"
  fi
}

# ── Kill any process already on a port ────────────────────────────────────────
free_port() {
  local port="$1"
  local pid
  pid=$(lsof -ti tcp:"$port" 2>/dev/null || true)
  if [ -n "$pid" ]; then
    echo -e "${YELLOW}Port ${port} in use by PID ${pid} — killing...${RESET}"
    kill -9 $pid 2>/dev/null || true
    sleep 1
  fi
}

# ── Cleanup on Ctrl+C ─────────────────────────────────────────────────────────
cleanup() {
  echo -e "\n${YELLOW}Shutting down all services...${RESET}"
  for pid in "${PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null
  echo -e "${GREEN}All services stopped.${RESET}"
  exit 0
}
trap cleanup SIGINT SIGTERM

# ── Start a dotnet service ────────────────────────────────────────────────────
start_dotnet() {
  local name="$1" dir="$2" color="$3"
  echo -e "${BOLD}${color}[${name}]${RESET} Starting..."
  ( cd "$dir" && env "${_ENV_EXTRAS[@]+"${_ENV_EXTRAS[@]}"}" dotnet run 2>&1 | while IFS= read -r line; do
      echo -e "${color}[${name}]${RESET} $line"
    done ) &
  PIDS+=($!)
}

# ── Start an ng serve ─────────────────────────────────────────────────────────
start_ng() {
  local name="$1" dir="$2" port="$3" color="$4"
  echo -e "${BOLD}${color}[${name}]${RESET} Starting on port ${port}..."
  ( cd "$dir" && npx ng serve --port "$port" 2>&1 | while IFS= read -r line; do
      echo -e "${color}[${name}]${RESET} $line"
    done ) &
  PIDS+=($!)
}

# ── Start a Python uvicorn service ────────────────────────────────────────────
start_python() {
  local name="$1" dir="$2" port="$3" color="$4"
  local pythonExe="$dir/.venv/bin/python3"
  if [ ! -f "$pythonExe" ]; then
    pythonExe="python3"
  fi
  echo -e "${BOLD}${color}[${name}]${RESET} Starting on port ${port}..."
  ( cd "$dir" && env "${_ENV_EXTRAS[@]+"${_ENV_EXTRAS[@]}"}" "$pythonExe" -m uvicorn app.main:app --host 0.0.0.0 --port "$port" 2>&1 | while IFS= read -r line; do
      echo -e "${color}[${name}]${RESET} $line"
    done ) &
  PIDS+=($!)
}

# ── Ensure the out-of-tree workflow-builder library resolves its node deps ─────
# ava-orchestration-ui consumes ../Lib/workflow-builder as source (via tsconfig
# "paths"). That library's bare @angular/*, rxjs and tslib imports need a
# reachable node_modules; since the library lives outside the UI project, symlink
# the UI's already-installed modules into it when the link is missing/broken.
link_workflow_builder_deps() {
  local lib="$ROOT/Lib/workflow-builder"
  local target="$ROOT/ava-orchestration-ui/node_modules"
  if [ -d "$lib" ] && [ -d "$target" ] && [ ! -d "$lib/node_modules" ]; then
    ln -sfn ../../ava-orchestration-ui/node_modules "$lib/node_modules"
    echo -e "${GREEN}Linked workflow-builder node_modules -> ava-orchestration-ui/node_modules${RESET}"
  fi
}

# ── Poll /health until 200 ────────────────────────────────────────────────────
wait_healthy() {
  local name="$1" url="$2" color="$3"
  echo -e "${color}[${name}]${RESET} Waiting for ${url} ..."
  until curl -sf "$url" > /dev/null 2>&1; do
    sleep 2
  done
  echo -e "${GREEN}[${name}]${RESET} ${BOLD}Healthy${RESET}"
}

# ── Main ──────────────────────────────────────────────────────────────────────
echo -e "${BOLD}AVA — Starting all services${RESET}\n"

# ── Load environment files ────────────────────────────────────────────────────
echo -e "${YELLOW}Loading .env files...${RESET}"
load_env_file "$ROOT/ava-kb/.env"
load_env_file "$ROOT/ava-api/.env"
echo -e "${GREEN}.env files loaded.${RESET}\n"

# ── Free all ports first ──────────────────────────────────────────────────────
echo -e "${YELLOW}Freeing ports...${RESET}"
free_port 8100
free_port 5010
free_port 6200
free_port 6201
free_port 9090
echo -e "${GREEN}Ports clear.${RESET}\n"

# 1. ava-kb (local Python service)
start_python "ava-kb" "$ROOT/ava-kb" 8100 "$MAGENTA"
wait_healthy "ava-kb" "http://localhost:8100/health" "$MAGENTA"

# 3. ava-api
start_dotnet  "ava-api"   "$ROOT/ava-api/Ava.Api"               "$BLUE"
wait_healthy  "ava-api"   "http://localhost:5010/health"         "$BLUE"

# 4. Angular apps (parallel — no health check needed)
link_workflow_builder_deps
start_ng "ava-web"            "$ROOT/ava-web"                6200 "$YELLOW"
start_ng "orchestration-ui"   "$ROOT/ava-orchestration-ui"   6201 "$DARKCYAN"

# 5. AVA loader (build element bundle, then serve for embedded integration)
echo -e "${DARKYELLOW}[ava-loader] Building element bundle...${RESET}"
( cd "$ROOT/ava-web" && npm run build:element )
echo -e "${GREEN}[ava-loader] Build succeeded${RESET}"

echo -e "${DARKYELLOW}[ava-loader] Starting on port 9090...${RESET}"
( cd "$ROOT/ava-web" && npx --yes http-server dist/element/browser --port 9090 --cors -s 2>&1 | while IFS= read -r line; do
    echo -e "${DARKYELLOW}[ava-loader]${RESET} $line"
  done ) &
PIDS+=($!)

echo -e "\n${BOLD}${GREEN}All services running:${RESET}"
echo -e "  ${MAGENTA}ava-kb${RESET}             http://localhost:8100"
echo -e "  ${BLUE}ava-api${RESET}            http://localhost:5010"
echo -e "  ${YELLOW}ava-web${RESET}            http://localhost:6200"
echo -e "  ${DARKCYAN}orchestration-ui${RESET}   http://localhost:6201"
echo -e "  ${DARKYELLOW}ava-loader${RESET}         http://localhost:9090"
echo -e "\n${BOLD}Press Ctrl+C to stop all services.${RESET}\n"

# Keep script alive
wait
