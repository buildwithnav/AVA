# AVA

AI Virtual Assistant — orchestration platform for agentic workflows.

## Repository Layout

```
repos/
├── AVA/                    ← this repo (API, UI, orchestration)
└── ADO MCP Server/         ← sibling repo (Azure DevOps MCP tools)
```

## Prerequisites

Both repos must be cloned as **siblings** in the same parent directory:

```bash
cd ~/source/repos          # or wherever you keep projects
git clone <ava-repo-url> AVA
git clone <mcp-repo-url> "ADO MCP Server"
```

If you have a non-standard layout, set the `ADO_MCP_PATH` environment variable to the
absolute path of the ADO MCP Server repo.

## Quick Start

```powershell
cd AVA
./start.ps1
```

This launches all services (API, knowledge base, Angular UIs).

## Services

| Service | Port | Description |
|---------|------|-------------|
| ava-kb | 8100 | Knowledge base / retrieval service |
| ava-api | 5010 | Orchestration API |
| ava-web | 6200 | Chat UI |
| orchestration-ui | 6201 | Workflow builder UI |
| ava-loader | 9090 | Embedded element bundle |

## MCP Integration

AVA connects to MCP servers via the **MCP Streamable HTTP protocol** (JSON-RPC 2025-03-26).
The active MCP server is **Context Forge**, configured under `Ava:Services:context-forge` in
`appsettings.json`. The gateway `BearerToken` must be supplied via `appsettings.Secrets.json`
(not committed to source control).

To connect a new MCP server: add an entry to `Ava:Services` with `"IsMcp": true` and set the
`BaseUrl`. The `ToolPrefix` field handles tool name prefixes added by federated gateways.
AVA discovers tools at runtime via `tools/list` — no code changes needed when tools change.
