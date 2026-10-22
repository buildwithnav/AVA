# Product Requirements Document
## AVA — AI Virtual Assistant

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | June 14, 2026 |
| **Status** | Draft |
| **Owner** | Product |

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement](#2-problem-statement)
3. [Goals & Success Metrics](#3-goals--success-metrics)
4. [User Personas](#4-user-personas)
5. [Functional Requirements](#5-functional-requirements)
6. [Non-Functional Requirements](#6-non-functional-requirements)
7. [Out of Scope](#7-out-of-scope)
8. [Integration Dependencies](#8-integration-dependencies)
9. [Risks](#9-risks)
10. [ADO Work Item Reference](#10-ado-work-item-reference)

---

## 1. Executive Summary

AVA (AI Virtual Assistant) is an enterprise AI orchestration platform for Abbott's Clinical Data Systems (CDS). It exposes clinical data, enterprise APIs, and structured multi-step workflows through a unified natural-language chat interface. AVA is designed to be embedded into any CDS web application with a single `<script>` loader tag, requiring no changes to the host application.

AVA is not a generic chatbot. It is a domain-scoped agentic workflow engine — it can extract data from enterprise APIs, ask users to make selections mid-workflow, generate structured outputs, query tabular datasets with LLM-generated SQL, and search reference documents — all in a single conversational session.

---

## 2. Problem Statement

Clinical data users at Abbott face the following friction points:

- **Fragmented data access** — Relevant data lives across eLogs, FDF API, and CDS Reporting. Users must navigate multiple systems manually to answer a single question.
- **Technical dependency** — Querying databases, calling APIs, or generating reports requires technical skills that most clinical data users do not have.
- **Repetitive multi-step tasks** — Common tasks (e.g., extract data → filter → generate a report) are performed manually and repeatedly with no automation layer.
- **No unified discovery surface** — There is no single place for users to discover what workflows, queries, or data sources are available to them.
- **Embedded experience gap** — Existing CDS web applications have no AI layer; users must leave their application context to find answers.

---

## 3. Goals & Success Metrics

### Goals

- Reduce time-to-insight for clinical data users through conversational access to enterprise data
- Enable non-technical users to query enterprise systems using natural language
- Automate multi-step data workflows within existing CDS web applications
- Provide a reusable, embeddable AI platform that can be adopted across all CDS applications

### Success Metrics

| Metric | Description |
|---|---|
| Monthly active users | Unique users per domain per month |
| Workflow completion rate | % of initiated workflows that reach confirmation/completion |
| Time-to-answer | Average duration from first message to final response |
| Token cost per session | Average USD cost per conversation session |
| Error / retry rate | % of agent runs that encounter a tool or query error |
| User satisfaction | NPS or CSAT score collected post-session |

---

## 4. User Personas

| Persona | Description | Primary Use Case |
|---|---|---|
| **Clinical Data User** | Non-technical CDS end user — clinical ops, data reviewers, study coordinators | Query eLogs data, run reports, search SOPs and reference documents via conversational chat |
| **Business Analyst / Product Owner** | Defines workflow requirements and data needs; no deep technical background | Configure domains, design workflows in the visual builder, define data templates, review usage analytics |
| **Engineer** | Builds and maintains AVA features and integrations | Register tools and MCP servers, manage knowledge base sources, troubleshoot API integrations, extend workflow stage types |
| **Platform Engineer** | IT / DevOps / Infrastructure | Azure infrastructure, CI/CD pipelines, PostgreSQL and Redis operations, authentication and security configuration |

---

## 5. Functional Requirements

### F1 — Conversational Chat Interface

The primary user-facing surface is a floating sidebar chat widget that can be injected into any host web application.

**Requirements:**
- Embed into any web application via a single `<script>` loader tag with `data-*` configuration attributes
- Support floating FAB button or injection into an existing host application header element
- Natural language input with real-time streaming responses via Server-Sent Events (SSE)
- Markdown rendering including code blocks, tables, and lists
- Domain-scoped welcome messages and suggested prompts displayed on session start
- Undo / rewind individual conversation turns
- Session persistence across page navigation within the same host application

---

### F2 — Multi-Step Agentic Workflow Engine

Workflows are pre-defined multi-stage execution plans triggered by natural language. The engine executes each stage in sequence, pausing for user interaction where required.

**Stage Types:**

| Stage Type | Description |
|---|---|
| `ai-extract` | LLM extracts structured fields from user input or prior results |
| `tool` | Execute a registered REST or MCP tool call |
| `user-select` | Pause execution and present a selection panel to the user |
| `ai-generate` | LLM generates a structured output (report, summary, recommendation) |
| `ai-analyze` | LLM analyzes tool results and produces an interpretation |
| `confirmation` | Present a confirmation dialog before a consequential action |
| `foreach` | Iterate a stage over a collection of items |
| `agentic` | Open-ended LLM agent loop with access to tools |

**Requirements:**
- Execute workflows as a stage-by-stage state machine
- Pause execution at `user-select` and `confirmation` stages and resume upon user response
- Support rewind to any prior completed stage within a run
- Persist workflow execution context (field values, stage cursor, outputs) for pause/resume
- PII check flag per workflow definition to prevent sensitive data from entering LLM context
- Token field substitution (`{{fieldName}}`) throughout workflow stage configuration

---

### F3 — Semantic Workflow Discovery

When a user sends a message, AVA identifies the most relevant workflow (if any) using a multi-strategy retrieval pipeline.

**Requirements:**
- Match user input to workflow definitions using vector (semantic) similarity and TF-IDF keyword search with hybrid ranking
- LLM-based routing to select the appropriate retrieval strategy: None, Fast, Expanded, or Agentic
- Fall back to conversational reply when no workflow matches above the confidence threshold
- Semantic search index rebuilt automatically when workflow definitions are added or updated
- Support domain-scoped workflow isolation — users only see workflows for their configured domain

---

### F4 — Enterprise API Tool Integration

Tools are registered REST endpoints or MCP servers that workflows and the agentic stage can call to fetch or submit data.

**Requirements:**
- Tool registry supporting GET, POST, PUT, and DELETE HTTP methods
- MCP (Model Context Protocol) tool execution via the Context Forge gateway
- CDS platform authentication via cookie forwarding (`cas-at` / `cas-rt`) — no separate AVA login required in production
- Response parsing for JSON, CSV, and XLSX formats
- Ability to store a tool's response directly as a session or domain-scoped knowledge base for downstream querying
- Tool archive and restore lifecycle — archived tools are not exposed to the agent
- Tools requiring user confirmation flagged at the definition level

---

### F5 — Structured Dataset Querying (Knowledge Base — Dataset)

Users and workflows can query tabular datasets using natural language. The LLM generates SQL; AVA executes it in a sandboxed DuckDB environment.

**Requirements:**
- Upload CSV, Excel, or Parquet datasets and persist as Parquet columnar storage
- LLM-generated SQL queries executed over datasets via DuckDB
- Column schema (name + data type) and semantic column definitions (human-readable label + description) injected into system prompt for every query
- 2 sample rows per dataset included in system prompt context
- SQL sandbox: `SELECT`-only; `DROP`, `INSERT`, `UPDATE`, `DELETE`, `CREATE TABLE`, `ATTACH`, and file reads blocked
- DuckDB external filesystem access disabled (`SET enable_external_access = false`)
- Query errors (including available table and column names) returned to the LLM for self-correcting retry
- Results capped at 1,000 rows with truncation flag
- Domain-scoped and session-scoped KB isolation
- Column definitions manageable by BA/PO via the Admin UI without engineering involvement

---

### F6 — Reference Document Search (Knowledge Base — Documents)

Users and workflows can search uploaded reference documents (SOPs, guides, reports) using natural language.

**Requirements:**
- Upload PDF, DOCX, and Markdown documents; automatically chunked into a parent-child hierarchy
- BM25 full-text search (DuckDB FTS) combined with vector embedding similarity, ranked using Reciprocal Rank Fusion (RRF)
- Parent-child expansion: child chunks retrieved for precision; parent section returned for LLM context
- Section-heading-aware lookup for named section queries
- Smart retrieval router selects strategy (None / Fast / Expanded / Agentic) based on query complexity and router confidence
- Domain-scoped document isolation — documents belong to a domain and are not shared across domains
- Per-document metadata: title, description, tags, uploader

---

### F7 — Domain Management

Domains are the primary multi-tenancy boundary in AVA. Each domain has its own set of workflows, tools, knowledge bases, and reference documents. Domains can be subdivided into **sub-domains** — for example, *Clinical Studies* contains one sub-domain per study — each with independent access control.

**Requirements:**
- Create, update, archive, and restore domains
- Assign knowledge bases, reference documents, and workflow suggestions per domain
- Configure domain-level welcome message and suggested prompts
- Enable or disable individual domains without deleting them
- Domain owner assignment for accountability
- All agent activity (chat, workflow execution, KB queries) is scoped to the active domain
- Create sub-domains under a domain, optionally linked to a source-system record via an external reference (e.g. a study ID)
- Scope a chat session to a specific sub-domain, or to the whole domain

---

### F7.1 — Module Access Control

Access is granted per domain and per sub-domain, mirroring the CDS Angular Template's module pattern. Each domain maps to a CDO Gateway module key; sub-domains are gated by AVA-native grants so a domain with dozens of studies doesn't require dozens of gateway registrations.

**Requirements:**
- Fetch the caller's module grants from the CDO Gateway and cache them per user
- Resolve caller identity from the CAS session so activity, usage, and grants attribute to a real user
- Gate each domain on a configurable module key; unmapped domains follow an explicit open/denied policy
- Grant users access to individual sub-domains or to all sub-domains in a domain via a wildcard
- Effective access is the intersection of domain module and sub-domain grant; a super-user module bypasses both
- Enforce access server-side on every domain-scoped endpoint — UI filtering is presentation only
- Hide inaccessible domains, sub-domains, pages, and nav items in both the chat element and the admin UI
- Support a local development mode with a configurable grant set so denial paths can be tested without the gateway

See [module-access.md](module-access.md) for the operational runbook.

---

### F8 — Admin UI: Visual Workflow Builder

A dedicated Angular-based administration interface for managing all AVA configuration without requiring code changes.

**Requirements:**
- **Domain management panel** — Create and configure domains, attach KBs and domain files, manage suggestions and welcome messages
- **Visual workflow builder** — Stage-by-stage designer with drag-and-drop reordering, per-stage configuration, and live test tab with real execution
- **AI-assisted workflow creation** — Describe a workflow in plain text and have AVA generate a draft workflow definition via streaming
- **Data template management** — Define reusable form templates with typed fields for use in workflow stages
- **Tool server management** — Register REST tools and MCP servers; view merged live tool list from DB and MCP gateway
- **Workflow test tab** — Execute any workflow definition directly (bypassing semantic matching) and observe stage-by-stage execution with live field outputs

---

### F9 — Multi-Model LLM Support

AVA supports multiple LLM providers and allows per-session model selection.

**Requirements:**
- **Azure OpenAI** — GPT-4.1 (primary), GPT-4o, GPT-4o-mini; `text-embedding-ada-002` for embeddings
- **GitHub Copilot** — Multi-model access: GPT-4o, Claude 3.5 Sonnet, Claude 3.7 Sonnet, Gemini; authenticated via GitHub OAuth Device Flow in-session
- Per-session model selection persisted in Redis; model preference survives page refresh within the session
- Workflow definitions can specify a model override for specific stages
- Embedding model (`text-embedding-ada-002`) used for semantic workflow search and domain file vector indexing

---

### F10 — Conversation & Session Management

**Requirements:**
- Session-scoped in-memory conversation history with ring-buffer management
- Automatic compaction: when history exceeds the threshold (14 messages), older turns are summarised using `gpt-4o-mini` and the summary replaces the raw messages; 6 most recent turns are always retained verbatim
- Maximum of 5 compaction snapshots stored per conversation for undo/rewind support
- Conversation archive in PostgreSQL — users can retrieve prior conversations and browse message history
- Agent run records — status, stage steps, token usage (input/output), estimated cost, matched workflow ID
- Session data (GitHub token, ADO PAT, selected model, active KB) stored in Redis with in-memory fallback

---

### F11 — Usage Analytics

**Requirements:**
- Record a usage event for every agent run: session ID, user ID, domain, model ID, input tokens, output tokens, estimated cost in USD
- Per-model cost estimation using configurable USD-per-million-token pricing
- Usage aggregation API: total cost, token counts, run counts — filterable by user, domain, and date range
- Per-user usage detail endpoint for individual consumption reporting
- Usage data exposed via API for integration with external dashboards or reporting tools

---

### F12 — Embeddable Loader Widget

**Requirements:**
- Drop-in integration: a single `<script src="ava-loader.js">` tag embeds AVA into any host application
- Configurable via `data-*` attributes on the script tag: domain, API URL, admin URL, user ID, display mode
- Supports floating FAB button mode or injection into an existing host application element
- In production, API and admin URLs are automatically derived from the loader script origin — no manual URL configuration required
- Fully isolated from the host application's JavaScript and CSS
- The compiled Angular chat element (`ava-chat.js`) can also be distributed and consumed as a standalone custom element

---

### F13 — Azure DevOps Integration

**Requirements:**
- ADO tools available to workflows and the agentic agent via the MCP (Model Context Protocol) Streamable HTTP protocol through the Context Forge gateway
- ADO tools discoverable through the standard tool registry — no special code path required
- Per-session ADO Personal Access Token (PAT) stored in the Redis session; not persisted to the database
- ADO PAT scoped to the user's session and cleared on session end
- Supports ADO work item queries, repository operations, and pipeline interactions as defined by the MCP gateway tool catalog

---

## 6. Non-Functional Requirements

| Category | Requirement |
|---|---|
| **Performance** | SSE streaming with first token delivered within 2 seconds of request receipt; max tool iterations per agent run capped at 15 |
| **Scalability** | All services containerised (Docker Compose for dev); architecture is compatible with Azure Kubernetes Service for horizontal scaling |
| **Security** | Production CDS authentication uses cookie forwarding only — no credentials stored in AVA; ADO PATs stored in Redis session only (never in the database); PII check flag available per workflow |
| **Reliability** | Redis session store has automatic in-memory fallback if Redis is unavailable; health check endpoints on all services (`/health`) |
| **Maintainability** | Database schema auto-migrated on API startup; zero-downtime configuration updates via `appsettings.json` without redeployment |
| **Observability** | Usage events, agent run records, per-model cost estimates, and structured application logging on all services |
| **Compliance** | Domain isolation, PII check flag, and session-scoped data handling support compliance requirements in a clinical data environment |

---

## 7. Out of Scope

The following are explicitly not in scope for the current version of AVA:

| Item | Notes |
|---|---|
| Mobile-native applications | AVA is a web-embedded widget; no native iOS or Android app |
| Database write operations | AVA is read-only with respect to enterprise data systems; it does not write back to CDS databases |
| Multi-user real-time collaboration | Conversations are single-user and session-scoped |
| Offline / disconnected mode | AVA requires network connectivity to LLM providers and CDS APIs |
| Self-hosted LLM inference | Only Azure OpenAI and GitHub Copilot are supported LLM providers |
| End-user workflow authoring | Workflow creation is an admin/engineering function; end users cannot define new workflows |

---

## 8. Integration Dependencies

| Dependency | Type | Purpose | Notes |
|---|---|---|---|
| **Azure OpenAI** | External SaaS | LLM inference and text embeddings | `gpt-4.1`, `gpt-4o`, `gpt-4o-mini`, `text-embedding-ada-002` deployments required |
| **GitHub Copilot API** | External SaaS | Multi-model LLM access (GPT, Claude, Gemini) | Optional; requires GitHub OAuth client ID; user authenticates in-session via Device Flow |
| **PostgreSQL + pgvector** | Infrastructure | Primary database; vector embeddings | Azure-hosted; `pgvector` extension required |
| **Redis** | Infrastructure | Session store (tokens, model selection, active KB) | Optional; AVA falls back to in-memory if unavailable |
| **Context Forge MCP Gateway** | Internal service | ADO and Git tool access via MCP protocol | Bearer token required; ADO PAT provided per user session |
| **Abbott CDS Platform APIs** | Internal APIs | eLogs, FDF API, CDS Reporting | Production auth via `cas-at`/`cas-rt` cookie forwarding; AVA deployed behind Pylon gateway |

---

## 9. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **LLM generates incorrect SQL on first attempt** | Low | Column schema, semantic column definitions, and 2 sample rows are injected into every system prompt. Query errors (including available table and column names) are returned to the LLM for self-correcting retry. DuckDB sandbox blocks all destructive SQL patterns. |
| **Token cost overruns from long agentic loops** | Medium | Maximum iteration cap of 15 per agent run. Per-run cost is tracked and recorded. Usage analytics provide visibility for early detection. |
| **MCP gateway unavailability affecting ADO operations** | Low | Tool availability is checked at call time. Agent surfaces a structured tool error and continues. ADO is an optional integration — core AVA functionality is unaffected. |
| **PII exposure in LLM context** | Medium | PII check flag available per workflow definition. Domain-scoped KB isolation prevents cross-domain data leakage. Engineers and BA/POs are responsible for configuring PII flags on sensitive workflows. |

---

## 10. ADO Work Item Reference

The following hierarchy maps PRD functional requirements to ADO Epics, Features, and User Stories. This is the recommended structure for loading work items into Azure DevOps.

> **Hierarchy:** Epic → Feature → User Story (PBI)
> Acceptance Criteria are attached at the User Story level.

---

### Epic: AVA — Enterprise AI Orchestration Platform

---

#### Feature: Chat Interface & Embedded Widget *(F1, F12)*

| # | User Story |
|---|---|
| 1.1 | As a CDS user, I can open the AVA sidebar from any CDS page without leaving the application |
| 1.2 | As a CDS user, I receive streaming responses in real time so I do not wait for the full answer before reading |
| 1.3 | As a CDS user, I can undo a conversation turn to go back to my previous state |
| 1.4 | As a Platform Engineer, I can embed AVA into any web application by adding a single `<script>` tag |
| 1.5 | As a Platform Engineer, I can configure AVA's domain, user ID, and display mode using `data-*` attributes on the loader script |

---

#### Feature: Agentic Workflow Engine *(F2)*

| # | User Story |
|---|---|
| 2.1 | As a CDS user, I can trigger a multi-step workflow using natural language without knowing the workflow's name |
| 2.2 | As a CDS user, I can make a selection from a presented list mid-workflow so the workflow continues with my chosen item |
| 2.3 | As a CDS user, I can confirm or cancel a consequential action before AVA proceeds |
| 2.4 | As a CDS user, I can rewind a workflow to a previous stage if I want to change an earlier input |
| 2.5 | As an Engineer, I can configure a PII check flag on a workflow to prevent sensitive fields from entering LLM context |

---

#### Feature: Semantic Workflow Discovery *(F3)*

| # | User Story |
|---|---|
| 3.1 | As a CDS user, my natural language message is automatically matched to the correct workflow without me selecting it from a menu |
| 3.2 | As a CDS user, when no workflow matches my message, AVA responds conversationally rather than returning an error |
| 3.3 | As an Engineer, the semantic search index is rebuilt automatically when a workflow definition is created or updated |

---

#### Feature: Enterprise Tool Integration *(F4)*

| # | User Story |
|---|---|
| 4.1 | As an Engineer, I can register a REST tool (GET/POST/PUT/DELETE) so it is available to workflows and the agentic agent |
| 4.2 | As an Engineer, I can register an MCP server so its tools are automatically discovered and available in the tool registry |
| 4.3 | As a CDS user, tool calls to CDS APIs are authenticated automatically using my existing session cookies — no separate login required |
| 4.4 | As an Engineer, I can configure a tool to store its response as a knowledge base so downstream workflow stages can query the result |
| 4.5 | As a BA/PO, I can archive a tool to remove it from active use without deleting its definition |

---

#### Feature: Structured Dataset Querying *(F5)*

| # | User Story |
|---|---|
| 5.1 | As a CDS user, I can upload a CSV or Excel file and immediately query it using natural language |
| 5.2 | As a CDS user, my dataset query returns accurate results even if I do not know the exact column names |
| 5.3 | As a BA/PO, I can define human-readable labels and descriptions for dataset columns so the LLM generates more accurate queries |
| 5.4 | As an Engineer, dataset SQL queries are sandboxed so the LLM cannot read files, write data, or execute destructive operations |

---

#### Feature: Reference Document Search *(F6)*

| # | User Story |
|---|---|
| 6.1 | As a CDS user, I can search uploaded SOPs and reference documents using natural language and receive relevant excerpts |
| 6.2 | As a CDS user, when I ask about a named section of a document, AVA returns the full section content |
| 6.3 | As a BA/PO, I can upload a PDF, DOCX, or Markdown file and it is automatically chunked and indexed for search |
| 6.4 | As a BA/PO, I can add a title, description, and tags to a document to improve search relevance |

---

#### Feature: Domain & Admin Management *(F7, F8)*

| # | User Story |
|---|---|
| 7.1 | As a BA/PO, I can create a domain and configure its knowledge bases, welcome message, and suggested prompts without engineering support |
| 7.2 | As a BA/PO, I can design a multi-step workflow using the visual workflow builder and test it without deploying code |
| 7.3 | As a BA/PO, I can describe a workflow in plain text and have AVA generate a draft workflow definition I can then edit |
| 7.4 | As a BA/PO, I can archive a domain to disable it without losing its configuration |
| 7.5 | As a BA/PO, I can define reusable data templates for use across workflow stages |

---

#### Feature: Multi-Model LLM Support *(F9)*

| # | User Story |
|---|---|
| 8.1 | As a CDS user, I can switch between available LLM models within my session without losing my conversation history |
| 8.2 | As a CDS user, I can authenticate with GitHub Copilot in-session to access Claude and Gemini models alongside GPT |
| 8.3 | As an Engineer, I can configure a model override on a specific workflow or stage to use a different model for that context |

---

#### Feature: Conversation & Session Management *(F10)*

| # | User Story |
|---|---|
| 9.1 | As a CDS user, my conversation history is available for the duration of my session without degrading response quality as the conversation grows |
| 9.2 | As a CDS user, I can browse my past conversations and review prior agent run outputs |
| 9.3 | As a Platform Engineer, long conversations are automatically compacted to control token costs without user intervention |

---

#### Feature: Usage Analytics *(F11)*

| # | User Story |
|---|---|
| 10.1 | As a Platform Engineer, I can view token usage and estimated USD cost aggregated by user, domain, and model |
| 10.2 | As a BA/PO, I can view usage metrics to understand which workflows and domains are most active |

---

#### Feature: Azure DevOps Integration *(F13)*

| # | User Story |
|---|---|
| 11.1 | As an Engineer, I can query ADO work items and repository data through AVA using natural language |
| 11.2 | As a CDS user, my ADO Personal Access Token is used only for my session and is never persisted to a database |
| 11.3 | As an Engineer, AVA degrades gracefully when the ADO MCP gateway is unavailable — core functionality is unaffected |
