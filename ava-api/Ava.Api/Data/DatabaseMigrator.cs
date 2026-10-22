using Dapper;
using Npgsql;

namespace Ava.Api.Data;

/// <summary>
/// Creates database schema and seeds baseline data. Idempotent and non-destructive:
/// schema uses IF NOT EXISTS and all seed inserts use ON CONFLICT DO NOTHING, so existing
/// rows are never overwritten. Opt-in — invoked from Program.cs only when RUN_MIGRATIONS is set.
/// </summary>
public class DatabaseMigrator
{
    private readonly DbConnectionFactory _db;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(DbConnectionFactory db, ILogger<DatabaseMigrator> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Create tables and enable pgvector extension. Idempotent.</summary>
    public async Task MigrateAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();

        _logger.LogInformation("Running database migration...");

        // Enable pgvector
        await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;");

        // Create tables
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS domains (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL,
                description     TEXT NOT NULL DEFAULT '',
                owner           TEXT NOT NULL DEFAULT '',
                enabled         BOOLEAN NOT NULL DEFAULT true,
                kb_ids          JSONB NOT NULL DEFAULT '[]',
                suggestions     JSONB NOT NULL DEFAULT '[]',
                welcome_message TEXT
            );

            CREATE TABLE IF NOT EXISTS prompts (
                id              TEXT PRIMARY KEY,
                domain          TEXT NOT NULL,
                description     TEXT NOT NULL DEFAULT '',
                context_tags    TEXT[] NOT NULL DEFAULT '{}',
                system_prompt   TEXT NOT NULL DEFAULT '',
                stages          JSONB NOT NULL DEFAULT '[]',
                pii_check       BOOLEAN NOT NULL DEFAULT false
            );

            CREATE TABLE IF NOT EXISTS tools (
                id                    TEXT PRIMARY KEY,
                service               TEXT NOT NULL DEFAULT '',
                route                 TEXT NOT NULL DEFAULT '',
                http_method           TEXT NOT NULL DEFAULT 'GET',
                description           TEXT,
                parameters            JSONB NOT NULL DEFAULT '[]',
                requires_confirmation BOOLEAN NOT NULL DEFAULT false,
                internal              BOOLEAN NOT NULL DEFAULT false,
                response_format       TEXT,
                store_as_kb           JSONB
            );

            CREATE TABLE IF NOT EXISTS templates (
                id          TEXT PRIMARY KEY,
                description TEXT NOT NULL DEFAULT '',
                fields      JSONB NOT NULL DEFAULT '[]'
            );

            CREATE TABLE IF NOT EXISTS prompt_embeddings (
                id           SERIAL PRIMARY KEY,
                prompt_id    TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                embedding    vector(1536) NOT NULL,
                created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(prompt_id, content_hash)
            );

            CREATE INDEX IF NOT EXISTS idx_prompts_domain ON prompts(domain);
        """);

        // Add content_type column for form-encoded tool support
        await conn.ExecuteAsync("ALTER TABLE tools ADD COLUMN IF NOT EXISTS content_type TEXT DEFAULT 'json';");

        // Add archived column for soft-delete of tools
        await conn.ExecuteAsync("ALTER TABLE tools ADD COLUMN IF NOT EXISTS archived BOOLEAN NOT NULL DEFAULT false;");

        // Add archived column for soft-delete of templates
        await conn.ExecuteAsync("ALTER TABLE templates ADD COLUMN IF NOT EXISTS archived BOOLEAN NOT NULL DEFAULT false;");

        // Add archived column for soft-delete of prompts/workflows
        await conn.ExecuteAsync("ALTER TABLE prompts ADD COLUMN IF NOT EXISTS archived BOOLEAN NOT NULL DEFAULT false;");

        // Add sample_prompt column for suggestion card starter text
        await conn.ExecuteAsync("ALTER TABLE prompts ADD COLUMN IF NOT EXISTS sample_prompt TEXT;");

        // Add preserve_context column — carry prior conversation into the workflow's AI stages
        await conn.ExecuteAsync("ALTER TABLE prompts ADD COLUMN IF NOT EXISTS preserve_context BOOLEAN NOT NULL DEFAULT false;");

        // Add agentic_instruction column — per-domain behavioral instruction injected into the system prompt
        await conn.ExecuteAsync("ALTER TABLE domains ADD COLUMN IF NOT EXISTS agentic_instruction TEXT;");

        // Add kb_prompt_mode column — controls how a domain's KBs are exposed in the system prompt
        // ("inline" full column dump, or "catalog" table names only + kb_schema on demand). NULL = inline.
        await conn.ExecuteAsync("ALTER TABLE domains ADD COLUMN IF NOT EXISTS kb_prompt_mode TEXT;");

        // ── Module access ────────────────────────────────────────────────────
        // Domains map to a CDO Gateway module key; sub-domains (e.g. individual studies)
        // are too numerous to register with the gateway, so their grants live in AVA.
        await conn.ExecuteAsync("ALTER TABLE domains ADD COLUMN IF NOT EXISTS module_key TEXT;");
        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_domains_module_key ON domains(module_key);");

        // Force-set module keys to the documented assignments (docs/module-access.md) on every
        // run, so any environment converges to the same keys as Ava:Security:LocalModules —
        // deliberately NOT gated on "IS NULL" since drift (e.g. stale/manual values) should self-heal.
        await conn.ExecuteAsync("""
            UPDATE domains SET module_key = v.module_key
            FROM (VALUES
                ('analyze-study',     'MxMthfJ'),
                ('ava-prompts',       'MdGlYAK'),
                ('ado',               'MOFeCFv'),
                ('clinical-data',     'MQSMhHZ'),
                ('ctms',              'M2u0e9Q'),
                ('mon-log',           'M4ecL7l'),
                ('study-pi-insight',  'MFxqRAV'),
                ('support',           'MtfeXmi'),
                ('InternalTesting',   'Mqp1yZl'),
                ('testing-analytics', 'M6aLTHE')
            ) AS v(id, module_key)
            WHERE domains.id = v.id;
        """);

        await MigrateDomainHierarchyAsync(conn);

        // Domain-scoped knowledge bases — persistent API-sourced datasets
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS knowledge_bases (
                id                  TEXT PRIMARY KEY,
                name                TEXT NOT NULL,
                description         TEXT,
                type                TEXT NOT NULL DEFAULT 'dataset',
                scope               TEXT NOT NULL DEFAULT 'domain',
                domain_id           TEXT,
                source              JSONB NOT NULL DEFAULT '{}',
                column_definitions  JSONB,
                row_count           INTEGER,
                chunk_count         INTEGER,
                schema              JSONB,
                sample_rows         JSONB,
                data_path           TEXT,
                enabled             BOOLEAN NOT NULL DEFAULT true,
                created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_refreshed_at   TIMESTAMPTZ
            );

            CREATE INDEX IF NOT EXISTS idx_knowledge_bases_domain ON knowledge_bases(domain_id);
        """);

        // Domain context files — persistent reference files uploaded per domain
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS domain_files (
                id              TEXT PRIMARY KEY,
                domain_id       TEXT NOT NULL,
                filename        TEXT NOT NULL,
                title           TEXT NOT NULL,
                description     TEXT NOT NULL DEFAULT '',
                tags            TEXT[] NOT NULL DEFAULT '{}',
                content_type    TEXT NOT NULL DEFAULT 'general',
                file_size_bytes BIGINT NOT NULL DEFAULT 0,
                chunk_count     INTEGER NOT NULL DEFAULT 0,
                uploaded_by     TEXT NOT NULL DEFAULT '',
                uploaded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                enabled         BOOLEAN NOT NULL DEFAULT true
            );

            CREATE INDEX IF NOT EXISTS idx_domain_files_domain ON domain_files(domain_id);
        """);

        // Chunk content table — stores text alongside embedding ID for retrieval
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS domain_file_chunks (
                collection  TEXT NOT NULL,
                chunk_id    TEXT NOT NULL,
                source      TEXT NOT NULL,
                title       TEXT NOT NULL DEFAULT '',
                content     TEXT NOT NULL,
                parent_id   TEXT,
                chunk_level TEXT NOT NULL DEFAULT 'child',
                chunk_number INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (collection, chunk_id)
            );
        """);

        // Migrate: add parent-child columns if they don't exist (safe for existing DBs)
        await conn.ExecuteAsync("""
            DO $$ BEGIN
                ALTER TABLE domain_file_chunks ADD COLUMN IF NOT EXISTS parent_id TEXT;
                ALTER TABLE domain_file_chunks ADD COLUMN IF NOT EXISTS chunk_level TEXT NOT NULL DEFAULT 'child';
                ALTER TABLE domain_file_chunks ADD COLUMN IF NOT EXISTS chunk_number INTEGER NOT NULL DEFAULT 0;
            EXCEPTION WHEN others THEN NULL;
            END $$;
        """);

        // Register unified search_docs tool
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, description, internal, parameters)
            VALUES (
                'search_docs',
                'Search domain reference documents for information. Returns relevant sections with full context. Use when you need specific information from uploaded reference files, whether searching by topic, section name, or following up on prior context.',
                true,
                '[{"name":"query","type":"string","required":true,"description":"What to search for in domain files"},{"name":"domainId","type":"string","required":true,"description":"The domain ID to search within"},{"name":"topK","type":"integer","required":false,"description":"Number of results to return (default 10)"}]'::jsonb
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // Remove legacy domain tools that have been replaced by search_docs
        await conn.ExecuteAsync("""
            DELETE FROM tools WHERE id IN ('domain_context_search', 'domain_section_lookup', 'domain_get_adjacent_chunks');
        """);

        // ── KB tools (internal — exposed to LLM for direct calling) ───────────────
        // Seed the tabular/document KB toolset so a fresh DB reproduces them. These
        // rows previously existed only in live databases. Insert-only (ON CONFLICT
        // DO NOTHING) so existing curated rows — e.g. an admin-edited kb_query
        // description — are never overwritten.
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, description, internal, parameters)
            VALUES (
                'kb_schema',
                'Get column names, types, and sample rows for a knowledge base. Call this before querying to understand the data structure.',
                true,
                '[{"name":"knowledgeBaseId","type":"string","required":true,"location":"body","description":"ID of the knowledge base to inspect"}]'::jsonb
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        await conn.ExecuteAsync("""
            INSERT INTO tools (id, description, internal, parameters)
            VALUES (
                'kb_query',
                'Query a tabular dataset using DuckDB SQL. Call kb_schema first to see available columns. Results shown to you are capped at 250 rows; when capped the response includes "truncated": true and "totalRowCount" (the full row count) — treat that as the signal to re-run with aggregation. The full result is always available to the user via the CSV download, so never widen a query just to see more rows. For analytical questions (outliers, distributions, totals, averages, min/max, highest/lowest, trends, counts), compute the answer in SQL using aggregation (COUNT, SUM, AVG, STDDEV, PERCENTILE_CONT, MEDIAN, MIN, MAX, GROUP BY) or ORDER BY <column> DESC/ASC LIMIT n — do not select raw rows and inspect them. For outliers, use statistical thresholds (e.g. values beyond mean +/- 2*STDDEV, or top/bottom percentiles via PERCENTILE_CONT) computed over the full table. Prefer selecting specific columns over SELECT *. After results return, interpret the numbers for the user rather than just restating the table. If a result is truncated, tell the user it was capped and re-run with aggregation for a complete answer.',
                true,
                '[{"name":"datasetIds","type":"array","required":true,"location":"body","description":"IDs of datasets to query. Multiple IDs enables JOINs."},{"name":"sql","type":"string","required":true,"location":"body","description":"SQL SELECT query"}]'::jsonb
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        await conn.ExecuteAsync("""
            INSERT INTO tools (id, description, internal, parameters)
            VALUES (
                'kb_search',
                'Search Q&A articles and support documents using natural language.',
                true,
                '[{"name":"knowledgeBaseId","type":"string","required":true,"location":"body","description":"ID of the document knowledge base to search"},{"name":"query","type":"string","required":true,"location":"body","description":"Natural language search query"},{"name":"topK","type":"integer","required":false,"location":"body","description":"Number of results to return (default 5)"}]'::jsonb
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── ADO query tools (internal — exposed to LLM for direct calling) ────────
        // These route through ExecuteInternalAsync → McpClientService → Context Forge gateway.
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, description, internal, parameters)
            VALUES (
                'get_ado_work_item',
                'Get an Azure DevOps work item by ID. Returns full details including title, type, state, description, assigned to, area/iteration paths, and relations (parent/child links). Use this when the user asks about a specific work item or wants to see its details and hierarchy.',
                true,
                '[{"name":"id","type":"integer","required":true,"description":"The work item ID number"},{"name":"$expand","type":"string","required":false,"description":"Expand relations to see linked work items. Use ''relations'' to include parent/child links."}]'::jsonb
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        await conn.ExecuteAsync("""
            INSERT INTO tools (id, description, internal, parameters)
            VALUES (
                'query_ado_work_items',
                'Execute a WIQL (Work Item Query Language) query against Azure DevOps to find work items. Use this to list children of a work item, search by criteria, or get backlog items. Returns matching work item IDs and fields.',
                true,
                '[{"name":"query","type":"string","required":true,"description":"WIQL query string. Example: SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State] FROM WorkItems WHERE [System.Parent] = 24889 ORDER BY [System.Id]"}]'::jsonb
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // NOTE: Legacy ado-mcp tool seed/migration statements were removed. ADO tools are
        // no longer stored in the DB — they are discovered live from the Context Forge MCP
        // gateway (service key "context-forge") via McpClientService and surfaced by
        // ToolsController. Any previously-seeded ado-mcp tool rows are removed by the
        // decommission migration further below.

        // ── Usage analytics ───────────────────────────────────────────────────────
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS usage_events (
                id                  BIGSERIAL PRIMARY KEY,
                agent_run_id        TEXT NOT NULL,
                conversation_id     TEXT,
                session_id          TEXT,
                user_id             TEXT NOT NULL DEFAULT '',
                username            TEXT NOT NULL DEFAULT '',
                domain              TEXT NOT NULL DEFAULT '',
                model_id            TEXT,
                input_tokens        INT NOT NULL DEFAULT 0,
                output_tokens       INT NOT NULL DEFAULT 0,
                total_tokens        INT NOT NULL DEFAULT 0,
                estimated_cost_usd  NUMERIC(12,6) NOT NULL DEFAULT 0,
                prompt_id           TEXT,
                created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_usage_events_user_id   ON usage_events(user_id);
            CREATE INDEX IF NOT EXISTS idx_usage_events_created_at ON usage_events(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_usage_events_model_id   ON usage_events(model_id);
        """);

        // ── Conversation archive ───────────────────────────────────────────────────
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS conversations (
                id          TEXT PRIMARY KEY,
                session_id  TEXT,
                user_id     TEXT NOT NULL DEFAULT '',
                username    TEXT NOT NULL DEFAULT '',
                domain      TEXT NOT NULL DEFAULT '',
                created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_conversations_user_id   ON conversations(user_id);
            CREATE INDEX IF NOT EXISTS idx_conversations_updated_at ON conversations(updated_at DESC);

            CREATE TABLE IF NOT EXISTS conversation_messages (
                id                  BIGSERIAL PRIMARY KEY,
                conversation_id     TEXT NOT NULL REFERENCES conversations(id),
                agent_run_id        TEXT,
                role                TEXT NOT NULL,
                content             TEXT NOT NULL,
                created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_conv_messages_conv_id      ON conversation_messages(conversation_id);
            CREATE INDEX IF NOT EXISTS idx_conv_messages_agent_run_id ON conversation_messages(agent_run_id);
        """);

        // Add username column for user tracking (Pylon unique_name) — for pre-existing DBs.
        await conn.ExecuteAsync("ALTER TABLE usage_events  ADD COLUMN IF NOT EXISTS username TEXT NOT NULL DEFAULT '';");
        await conn.ExecuteAsync("ALTER TABLE conversations ADD COLUMN IF NOT EXISTS username TEXT NOT NULL DEFAULT '';");

        await BackfillConfirmationRewindToAsync(conn);

        // ── Decommission ado-mcp and study-api ────────────────────────────────────
        // The ado-mcp service has been replaced by the Context Forge MCP gateway.
        // The study-api service has been decommissioned with no replacement.
        // Remove all associated tool records and workflows so they no longer appear
        // in the tool registry or workflow builder.
        var deletedTools = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tools WHERE service IN ('ado-mcp', 'study-api')");
        if (deletedTools > 0)
        {
            await conn.ExecuteAsync("DELETE FROM tools WHERE service IN ('ado-mcp', 'study-api')");
            _logger.LogInformation("Decommission migration: deleted {Count} ado-mcp/study-api tool records", deletedTools);
        }

        // NOTE: 'ado' workflows are intentionally NOT deleted here — they are curated in the
        // database and re-seeded below with ON CONFLICT DO NOTHING (insert-only), so any live
        // edits are preserved. Only the fully decommissioned 'study' domain is cleaned up.
        var deletedPrompts = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM prompts WHERE domain = 'study'");
        if (deletedPrompts > 0)
        {
            await conn.ExecuteAsync("DELETE FROM prompts WHERE domain = 'study'");
            _logger.LogInformation("Decommission migration: deleted {Count} legacy study workflow records", deletedPrompts);
        }

        // ── Clinical Data domain ──────────────────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO domains (id, name, description, owner, enabled, kb_ids, suggestions, welcome_message)
            VALUES (
                'clinical-data',
                'Clinical Data',
                'Clinical site data including site details, IRB/EC information, protocol revisions, and site operational status',
                'platform-team',
                true,
                '["clinical-data-sites"]'::jsonb,
                '["Show me all active sites for a study","Which sites have pending IRB approvals?","List sites by enrollment status"]'::jsonb,
                'I can help you query clinical site data including site details, IRB/EC information, and protocol revision status.'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── CDS Site Details Extract tool ─────────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type, response_format)
            VALUES (
                'cds-site-details-extract',
                'cds-reporting',
                '/core/api/report_mgmt/reporting/export',
                'POST',
                'Exports study-site level clinical trial data from CDS reporting including site details, IRB/EC information, approval dates, and protocol revision history.',
                '[{"name":"key","type":"string","required":true,"location":"body","defaultValue":"site_details_extract","description":"Report key identifier"},{"name":"value","type":"string","required":true,"location":"body","defaultValue":"{\"pStudyId\":\"0\"}","description":"JSON string of report parameters. pStudyId: study ID filter (0 = all studies)"}]'::jsonb,
                'json',
                'xlsx'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── CDS Patient Details Extract tool ──────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type, response_format)
            VALUES (
                'cds-patient-details-extract',
                'cds-reporting',
                '/core/api/report_mgmt/reporting/export',
                'POST',
                'Exports patient-level clinical trial data from CDS reporting including study and site identifiers, patient demographics, consent/enrollment/procedure dates, randomization and cohort assignments, device implant details, withdrawal/death status, and monitoring dates.',
                '[{"name":"key","type":"string","required":true,"location":"body","defaultValue":"patient_details_extract","description":"Report key identifier"},{"name":"value","type":"string","required":true,"location":"body","defaultValue":"{\"pStudyId\":\"0\"}","description":"JSON string of report parameters. pStudyId: study ID filter (0 = all studies)"}]'::jsonb,
                'json',
                'xlsx'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Add clinical-data-patients to Clinical Data domain kb_ids ─────────────
        await conn.ExecuteAsync("""
            UPDATE domains
            SET kb_ids = kb_ids || '["clinical-data-patients"]'::jsonb
            WHERE id = 'clinical-data'
              AND NOT (kb_ids @> '["clinical-data-patients"]'::jsonb);
        """);

        // ── Clinical Site domain ───────────────────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO domains (id, name, description, owner, enabled, kb_ids, suggestions, welcome_message)
            VALUES (
                'clinical-site',
                'Clinical Site',
                'Clinical site and patient data including site details, IRB/EC information, patient demographics, protocol revisions, site requirement tracking, and activation milestones.',
                'platform-team',
                true,
                '["clinical-site-sites","clinical-site-patients","clinical-site-requirements"]'::jsonb,
                '["Show sites missing required agreements","Which sites have incomplete activation checklists?","List patients by study and site","Show sites where financial agreement is pending"]'::jsonb,
                'I can help you query clinical site details, patient records, and site requirement milestones including regulatory, contractual, training, and activation status.'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── CDS Site Requirement Extract tool ─────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type, response_format)
            VALUES (
                'cds-site-requirement-extract',
                'cds-reporting',
                '/core/api/report_mgmt/reporting/export',
                'POST',
                'Exports site requirement tracking data from CDS reporting including regulatory and IRB/EC submissions, contractual agreements, certifications, training requirements, activation milestones, enrollment planning, sub-study readiness, and closeout documentation.',
                '[{"name":"key","type":"string","required":true,"location":"body","defaultValue":"site_requirement_extract","description":"Report key identifier"},{"name":"value","type":"string","required":true,"location":"body","defaultValue":"{\"pStudyId\":\"0\"}","description":"JSON string of report parameters. pStudyId: study ID filter (0 = all studies)"}]'::jsonb,
                'json',
                'xlsx'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── CDS Site SO Personnel Extract tool ────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type, response_format)
            VALUES (
                'cds-site-so-personnel-extract',
                'cds-reporting',
                '/core/api/report_mgmt/reporting/export',
                'POST',
                'Exports Abbott personnel assignments to clinical studies and study sites from CDS reporting, including role definitions, assignment history, assignment status, and organizational responsibility. Contains both study-level and site-level roles, enabling teams to identify who is responsible for a study or site, track role coverage, monitor resource allocation, and analyze staffing changes over time.',
                '[{"name":"key","type":"string","required":true,"location":"body","defaultValue":"so_personnel","description":"Report key identifier"},{"name":"value","type":"string","required":true,"location":"body","defaultValue":"{\"pStudyId\":\"0\"}","description":"JSON string of report parameters. pStudyId: study ID filter (0 = all studies)"}]'::jsonb,
                'json',
                'xlsx'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── CDS Site STS Master Extract tool ──────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type, response_format)
            VALUES (
                'cds-site-sts-master-extract',
                'cds-reporting',
                '/core/api/report_mgmt/reporting/export',
                'POST',
                'Exports study and site performance master data from CDS reporting for monitoring overall clinical study and site performance, site startup and activation efficiency, enrollment and recruitment progress, patient retention and follow-up compliance, protocol deviations and site quality, data discrepancy and query management, and safety reporting performance. Supports operational dashboards, KPIs, and executive reporting, and helps identify high-performing and underperforming clinical sites to drive clinical operations decisions and resource allocation.',
                '[{"name":"key","type":"string","required":true,"location":"body","defaultValue":"sts_master","description":"Report key identifier"},{"name":"value","type":"string","required":true,"location":"body","defaultValue":"{\"pStudyId\":\"0\"}","description":"JSON string of report parameters. pStudyId: study ID filter (0 = all studies)"}]'::jsonb,
                'json',
                'xlsx'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── CDS Site Nomination Header Extract tool ───────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type, response_format)
            VALUES (
                'cds-site-nom-header-extract',
                'cds-reporting',
                '/core/api/report_mgmt/reporting/export',
                'POST',
                'Exports site nomination and qualification header data from CDS reporting — study-level details (study code and title), site information (site identifiers, name, location, and principal investigator), workflow and approval data (submission dates, approval status, approvers, disapproval reasons, and form status), and participation outcomes (whether a site declined participation and the reason). One row per site nomination (NOM_ID, which joins to the nomination details dataset), enabling analysis of site selection, submission trends, approval decisions, and overall site feasibility and readiness for clinical trial participation.',
                '[{"name":"key","type":"string","required":true,"location":"body","defaultValue":"site_nom_header","description":"Report key identifier"},{"name":"value","type":"string","required":true,"location":"body","defaultValue":"{\"pStudyId\":\"0\"}","description":"JSON string of report parameters. pStudyId: study ID filter (0 = all studies)"}]'::jsonb,
                'json',
                'xlsx'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── CDS Site Nomination Details Extract tool ──────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type, response_format)
            VALUES (
                'cds-site-nom-details-extract',
                'cds-reporting',
                '/core/api/report_mgmt/reporting/export',
                'POST',
                'Exports the individual nomination and qualification form questions and responses from CDS reporting: NOM_ID (joins to the site nomination header), QUESTION_ID, QUESTION_LABEL, DISP_SEQ (display sequence), MANDATORY_FG (mandatory indicator), and QUESTION_RESPONSE. Enables detailed analysis of site selection criteria, qualification assessments, and per-question responses collected during site nomination.',
                '[{"name":"key","type":"string","required":true,"location":"body","defaultValue":"site_nom_details","description":"Report key identifier"},{"name":"value","type":"string","required":true,"location":"body","defaultValue":"{\"pStudyId\":\"0\"}","description":"JSON string of report parameters. pStudyId: study ID filter (0 = all studies)"}]'::jsonb,
                'json',
                'xlsx'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Add clinical-site-sts-master to Clinical Site domain kb_ids ───────────
        await conn.ExecuteAsync("""
            UPDATE domains
            SET kb_ids = kb_ids || '["clinical-site-sts-master"]'::jsonb
            WHERE id = 'clinical-site'
              AND NOT (kb_ids @> '["clinical-site-sts-master"]'::jsonb);
        """);

        // ── Add clinical-site-so-personnel to Clinical Site domain kb_ids ─────────
        await conn.ExecuteAsync("""
            UPDATE domains
            SET kb_ids = kb_ids || '["clinical-site-so-personnel"]'::jsonb
            WHERE id = 'clinical-site'
              AND NOT (kb_ids @> '["clinical-site-so-personnel"]'::jsonb);
        """);

        // ── Add clinical-site-nomination-header to Clinical Site domain kb_ids ────
        await conn.ExecuteAsync("""
            UPDATE domains
            SET kb_ids = kb_ids || '["clinical-site-nomination-header"]'::jsonb
            WHERE id = 'clinical-site'
              AND NOT (kb_ids @> '["clinical-site-nomination-header"]'::jsonb);
        """);

        // ── Add clinical-site-nomination-details to Clinical Site domain kb_ids ───
        await conn.ExecuteAsync("""
            UPDATE domains
            SET kb_ids = kb_ids || '["clinical-site-nomination-details"]'::jsonb
            WHERE id = 'clinical-site'
              AND NOT (kb_ids @> '["clinical-site-nomination-details"]'::jsonb);
        """);

        // ── Support domain (with agentic instruction to offer ticket creation) ────
        await conn.ExecuteAsync("""
            INSERT INTO domains (id, name, description, owner, enabled, kb_ids, suggestions, welcome_message, agentic_instruction)
            VALUES (
                'support',
                'Support',
                'EDC portal, eForms, eLogs, account access, and general clinical-systems support. Answer questions from the support knowledge base and help users open support tickets.',
                'platform-team',
                true,
                '[]'::jsonb,
                '["How do I reset my password?","I cannot access the EDC portal","Create a support request"]'::jsonb,
                'I can help answer support questions about the EDC portal, eForms, eLogs, and account access — and I can open a support ticket for you when you need one.',
                'You are AVA acting as a clinical-systems support assistant. First, try to resolve the user''s issue using the support knowledge base and tools. When a user describes a problem you cannot fully resolve — or after you have helped and the issue may still need follow-up — PROACTIVELY OFFER to create a support request ticket for them (e.g. "Would you like me to open a support ticket for this?"). Do NOT auto-launch the support-request workflow for general questions. Only call start_workflow with promptId="create-support-request" once the user agrees to create a ticket OR explicitly asks to create/open/submit one. When you do start it, the workflow will use the prior conversation to pre-fill the ticket, so the user should not need to repeat themselves.'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Analyze Study domain (queryable CRF datasets, catalog KB prompt mode) ──
        // Datasets are loaded via POST /api/kb/{id}/upload-file (see the study loader). Forms are
        // combined at the subject level on the "PT" column ("DOCNUM" is a per-record id that only
        // links a form to its own sub-forms). kb_prompt_mode='catalog' keeps the prompt lightweight
        // — the model calls kb_schema on demand rather than receiving every column inline.
        await conn.ExecuteAsync("""
            INSERT INTO domains (id, name, description, owner, enabled, kb_ids, suggestions, welcome_message, agentic_instruction, kb_prompt_mode)
            VALUES (
                'analyze-study',
                'Analyze Study',
                'Explore and analyze clinical study 1066 CRF datasets with natural language. AVA queries the underlying data (adverse events, demographics, concomitant medications, labs, and more) and joins datasets by subject where needed.',
                'platform-team',
                true,
                '[]'::jsonb,
                '["How many subjects are in the study?","How many serious adverse events were reported?","What are the most common concomitant medications?","Show adverse events broken down by sex"]'::jsonb,
                'I can help you explore study 1066. Ask me about adverse events, demographics, medications, labs, and more — I''ll query the study datasets and combine them by subject when needed.',
                'You are AVA helping analysts explore clinical study 1066. The knowledge bases are CRF (case report form) datasets that all describe the same study subjects. Every dataset shares a subject identifier column named "PT" (the patient/subject id). To combine data across different forms at the subject level, JOIN on PT (e.g. a."PT" = b."PT"). The "DOCNUM" column is a per-record document id, not a subject key: use it only to join a form to its own sub-forms (for example the AE dataset "as-1066-ae" to detail datasets such as "as-1066-aelab", "as-1066-aetest", "as-1066-aetrt"), never for subject-level joins across unrelated forms. Exception: the AE Financial Adjudication dataset (id "as-1066-ae-fin-adj") has neither PT nor DOCNUM — join it to the AE dataset on f."DOCUMENT_NUMBER" = a."DOCNUM", then reach subject data through the AE dataset PT. The system prompt lists the available datasets (table name + row count). Before writing SQL against a dataset, call kb_schema with its id to see the exact column names and their clinical definitions, then run kb_query with DuckDB SQL. Always double-quote table and column names.',
                'catalog'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Support Request tool ──────────────────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters, content_type)
            VALUES (
                'create-support-request',
                'support-api',
                '/api/support',
                'POST',
                'Creates a support request ticket. Provide title, description, priority, and category extracted from the user''s issue.',
                '[
                  {"name":"title","type":"string","required":true,"location":"body","description":"Short summary of the issue (max 100 characters)"},
                  {"name":"description","type":"string","required":true,"location":"body","description":"Detailed description of the problem"},
                  {"name":"priority","type":"string","required":false,"location":"body","description":"Priority level: P1 - Critical, P2 - High, P3 - Medium, P4 - Low"},
                  {"name":"category","type":"string","required":false,"location":"body","description":"Issue category: Account Access, Password Reset, EDC Portal, RDC Error, eForms, eLogs, Other"}
                ]'::jsonb,
                'json'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Support Request workflow ──────────────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO prompts (id, domain, description, context_tags, system_prompt, stages, pii_check, sample_prompt, preserve_context)
            VALUES (
                'create-support-request',
                'support',
                'Create a support request ticket — only trigger when the user explicitly asks to create, open, or submit a ticket or support request, not for general KB questions',
                ARRAY[
                    'create support request','open a ticket','submit a ticket','log an issue',
                    'raise a ticket','create a ticket','file a ticket','open support ticket',
                    'submit support request','log a support request','create case',
                    'escalate issue','report an issue','I need support',
                    'need to open a ticket','want to create a ticket',
                    'can you create a ticket','please create a support request'
                ],
                'You are a support request assistant. Help users create clear and accurate support tickets based on their reported issues.',
                $$[
                  {
                    "stageId": "extract",
                    "type": "ai-extract",
                    "required": ["title", "description"],
                    "prompt": "Extract support request details from the conversation. Use the entire prior conversation (shown as context) AND the user's latest message to infer the issue — the user may have described the problem in earlier turns before asking to create a ticket. Return ONLY a raw JSON object — no markdown, no tables, no explanations, no text before or after. The response must start with { and end with }. Nothing else.\n\nFields to extract:\n- title (string): A concise summary of the issue (max 100 characters). Return null if not determinable.\n- description (string): A clear description of the problem, drawing on the full conversation. Return null if not determinable.\n- priority (string): One of 'P1 - Critical', 'P2 - High', 'P3 - Medium', 'P4 - Low'. Default to 'P3 - Medium' if not specified.\n- category (string): One of 'Account Access', 'Password Reset', 'EDC Portal', 'RDC Error', 'eForms', 'eLogs', 'Other'. Infer from the description. Default to 'Other' if unclear.\n\nIMPORTANT: Do not describe the data. Do not return tables or narrative. Return ONLY the JSON object.",
                    "confirmEach": false
                  },
                  {
                    "stageId": "preview",
                    "type": "ai-generate",
                    "prompt": "Present a clear preview of the support request to the user before it is submitted:\n\n**Support Request Preview**\n- **Title**: {{title}}\n- **Description**: {{description}}\n- **Priority**: {{priority}}\n- **Category**: {{category}}\n\nTell the user to reply 'yes' to submit or describe any changes they want to make.",
                    "confirmEach": false
                  },
                  {
                    "stageId": "submit",
                    "type": "tool",
                    "toolId": "create-support-request",
                    "parameters": {
                      "title": "{{title}}",
                      "description": "{{description}}",
                      "priority": "{{priority}}",
                      "category": "{{category}}"
                    },
                    "confirmEach": false
                  },
                  {
                    "stageId": "result",
                    "type": "ai-analyze",
                    "prompt": "The support request was submitted successfully. Present a friendly confirmation to the user showing the ticket ID and a brief summary. Let them know the support team will review it and respond within the SLA for their priority level. Include: ticket ID, title, priority, and category.",
                    "dataFrom": ["submit"],
                    "confirmEach": false
                  }
                ]$$::jsonb,
                false,
                'I want to create a support request',
                true
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── ADO domain, tool, and workflows ──────────────────────────────────────
        // Insert-only (ON CONFLICT DO NOTHING): seeds the ado domain/tool/workflows on a
        // fresh DB, and preserves any existing curated rows on re-run.
        await SeedAdoAsync(conn);

        _logger.LogInformation("Database migration complete");
    }

    /// <summary>
    /// Domains gate access via a CDO Gateway module key; a domain's children (e.g. individual
    /// studies under "Clinical Studies") reuse the same <c>domains</c> row shape via a
    /// self-referencing <c>parent_id</c>, since CDS can't scale to a module key per study.
    /// Nesting is capped at 2 tiers — a child domain's <c>parent_id</c> must point at a
    /// top-level domain (one with no parent of its own); enforced in application code
    /// (AccessService / DomainController), not a DB constraint, matching how the rest of this
    /// schema favors app-level validation over triggers/CHECKs.
    ///
    /// One-time migration: if the legacy <c>sub_domains</c> / <c>sub_domain_grants</c> tables
    /// from the earlier two-table design are still present, their rows are folded into
    /// <c>domains</c> / <c>domain_grants</c> and the old tables are dropped. Safe to run
    /// repeatedly — the legacy tables simply won't exist on subsequent runs.
    /// </summary>
    private async Task MigrateDomainHierarchyAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("ALTER TABLE domains ADD COLUMN IF NOT EXISTS parent_id TEXT REFERENCES domains(id) ON DELETE CASCADE;");
        await conn.ExecuteAsync("ALTER TABLE domains ADD COLUMN IF NOT EXISTS external_ref TEXT;");
        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_domains_parent ON domains(parent_id);");

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS domain_grants (
                id             TEXT PRIMARY KEY,
                parent_id      TEXT NOT NULL REFERENCES domains(id) ON DELETE CASCADE,
                domain_id      TEXT REFERENCES domains(id) ON DELETE CASCADE,
                principal_type TEXT NOT NULL DEFAULT 'user',
                principal_id   TEXT NOT NULL,
                browse         BOOLEAN NOT NULL DEFAULT true,
                update_access  BOOLEAN NOT NULL DEFAULT false,
                insert_access  BOOLEAN NOT NULL DEFAULT false,
                delete_access  BOOLEAN NOT NULL DEFAULT false,
                UNIQUE(parent_id, domain_id, principal_type, principal_id)
            );

            -- domain_id IS NULL means a wildcard grant ("every child of parent_id"). The UNIQUE
            -- constraint above treats NULLs as distinct, so a partial index is needed to stop
            -- duplicate wildcard rows for the same parent/principal.
            CREATE UNIQUE INDEX IF NOT EXISTS idx_domain_grants_wildcard
                ON domain_grants(parent_id, principal_type, principal_id) WHERE domain_id IS NULL;

            CREATE INDEX IF NOT EXISTS idx_domain_grants_principal ON domain_grants(principal_id, principal_type);
            CREATE INDEX IF NOT EXISTS idx_domain_grants_parent ON domain_grants(parent_id);
        """);

        var legacyTablesExist = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'sub_domains')");
        if (!legacyTablesExist)
            return;

        await conn.ExecuteAsync("""
            INSERT INTO domains (id, name, description, owner, enabled, kb_ids, suggestions,
                                  welcome_message, agentic_instruction, parent_id, external_ref)
            SELECT id, name, description, '', enabled, kb_ids, suggestions,
                   welcome_message, agentic_instruction, domain_id, external_ref
            FROM sub_domains
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO domain_grants (id, parent_id, domain_id, principal_type, principal_id,
                                       browse, update_access, insert_access, delete_access)
            SELECT id, domain_id, NULLIF(sub_domain_id, '*'), principal_type, principal_id,
                   browse, update_access, insert_access, delete_access
            FROM sub_domain_grants
            ON CONFLICT DO NOTHING;
        """);

        await conn.ExecuteAsync("DROP TABLE IF EXISTS sub_domain_grants; DROP TABLE IF EXISTS sub_domains;");
        _logger.LogInformation("Migrated legacy sub_domains/sub_domain_grants into domains/domain_grants and dropped the old tables");
    }

    /// <summary>
    /// Backfill: every top-level confirmation stage with no rewindTo defaults to the
    /// immediately preceding stage's id, so that user corrections rewind one stage back.
    /// Idempotent — confirmations that already have a rewindTo (or are first in the
    /// pipeline) are left untouched. Users can still point it at an earlier stage in the UI.
    /// </summary>
    private async Task BackfillConfirmationRewindToAsync(NpgsqlConnection conn)
    {
        var rows = await conn.QueryAsync("SELECT id, stages::text AS stages FROM prompts");

        var updated = 0;
        foreach (var row in rows)
        {
            string id = row.id;
            string? stagesText = row.stages;
            if (string.IsNullOrWhiteSpace(stagesText)) continue;

            System.Text.Json.Nodes.JsonArray? stages;
            try
            {
                stages = System.Text.Json.Nodes.JsonNode.Parse(stagesText) as System.Text.Json.Nodes.JsonArray;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (stages == null) continue;

            var changed = false;
            for (int i = 1; i < stages.Count; i++)
            {
                if (stages[i] is not System.Text.Json.Nodes.JsonObject stage) continue;

                var type = stage["type"]?.GetValue<string>();
                if (!string.Equals(type, "confirmation", StringComparison.OrdinalIgnoreCase)) continue;

                var existing = stage["rewindTo"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(existing)) continue;

                var prevStageId = (stages[i - 1] as System.Text.Json.Nodes.JsonObject)?["stageId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(prevStageId)) continue;

                stage["rewindTo"] = prevStageId;
                changed = true;
            }

            if (changed)
            {
                await conn.ExecuteAsync(
                    "UPDATE prompts SET stages = @Stages::jsonb WHERE id = @Id",
                    new { Id = id, Stages = stages.ToJsonString() });
                updated++;
            }
        }

        if (updated > 0)
            _logger.LogInformation("Backfilled rewindTo on confirmation stages in {Count} prompt(s)", updated);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ── ADO domain, tool, and workflows ──────────────────────────────────────────
    // Insert-only seed (ON CONFLICT DO NOTHING): creates the ado domain/tool/workflows
    // when missing; existing curated rows are left untouched on re-run.
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task SeedAdoAsync(NpgsqlConnection conn)
    {
        // ── ADO domain ────────────────────────────────────────────────────────────
        await conn.ExecuteAsync("""
            INSERT INTO domains (id, name, description, owner, enabled, kb_ids, suggestions, welcome_message, agentic_instruction)
            VALUES (
                'ado',
                'Azure DevOps',
                'Create and manage Azure DevOps work items (Epics, Features, PBIs, Bugs, Tasks), decompose PRDs into work item hierarchies, query backlogs, check pipelines, and manage pull requests.',
                'platform-team',
                true,
                '[]'::jsonb,
                '["Create an Epic","Create a Feature","Create a PBI","Decompose a PRD into work items"]'::jsonb,
                'I can help you create and manage Azure DevOps work items, decompose PRDs into Epic/Feature/PBI hierarchies, query backlogs, and more.',
                'You are AVA acting as an Azure DevOps assistant. You help users create work items, query work item details, and decompose documents into ADO work item hierarchies. ROUTING RULES (check in order, first match wins): RULE 1 (PRD): If the user pastes a document (PRD, requirements, specs) → call start_workflow with promptId="decompose-prd" immediately. RULE 2 (ATTACH TO EXISTING): If the message mentions ANY existing parent work item by ID (#1234) or by name ("under epic X", "add to feature Y", "for the epic named Z", "create X for epic Y", "under the epic") — even if it also says "create" — call start_workflow with promptId="attach-to-existing". Trigger signals: "under", "add to", "attach to", "for epic/feature/pbi", "create X under Y", "add children to". RULE 3 (CREATE STANDALONE): ONLY if there is absolutely NO reference to any parent — user wants one new standalone item ("create an epic", "make a PBI called X") → call start_workflow with promptId="create-work-item". RULE 4 (QUERY): If the user asks about an existing work item ("show me epic #24889", "what are the children of #1234") → use get_ado_work_item and query_ado_work_items tools to fetch and display details. RULE 5 (OTHER): Answer conversationally.'
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── create-ado-work-item tool (Context Forge MCP gateway) ─────────────────
        // Routes to context-forge service (IsMcp=true). The executor strips the
        // "mcp:" prefix → mcpToolName="create_work_item" → McpClientService resolves
        // that to the gateway's "ado-mcp-server-create-work-item" via prefix-stripped
        // canonical name map. EnrichMcpParameters auto-injects Feature custom fields
        // (Custom.Rel_Purpose, Custom.Source) when type="Feature".
        await conn.ExecuteAsync("""
            INSERT INTO tools (id, service, route, http_method, description, parameters)
            VALUES (
                'create-ado-work-item',
                'context-forge',
                'mcp:create_work_item',
                'MCP',
                'Create an Azure DevOps work item (Epic, Feature, Product Backlog Item, Bug, or Task). Returns the created work item JSON including the id field.',
                '[
                  {"name":"type","type":"string","required":true,"location":"body","description":"Work item type: Epic, Feature, Product Backlog Item, Bug, or Task"},
                  {"name":"title","type":"string","required":true,"location":"body","description":"Work item title"},
                  {"name":"description","type":"string","required":false,"location":"body","description":"Description in HTML format. Use <p>, <ul>/<li>, <b>, <br> tags. Do not use markdown."},
                  {"name":"acceptanceCriteria","type":"string","required":false,"location":"body","description":"Acceptance criteria in HTML. Use BDD Given/When/Then style with <strong> keywords."},
                  {"name":"areaPath","type":"string","required":false,"location":"body","description":"Area path (e.g. CDS\\\\The Wizards)"},
                  {"name":"iterationPath","type":"string","required":false,"location":"body","description":"Iteration path (e.g. CDS\\\\Sprint 2026-01)"},
                  {"name":"parentId","type":"number","required":false,"location":"body","description":"Parent work item ID to link under"},
                  {"name":"priority","type":"number","required":false,"location":"body","description":"Priority 1-4 (1=Critical, 4=Low)"},
                  {"name":"assignedTo","type":"string","required":false,"location":"body","description":"Assignee display name or email"},
                  {"name":"tags","type":"string","required":false,"location":"body","description":"Comma-separated tags"},
                  {"name":"effort","type":"number","required":false,"location":"body","description":"Story points / effort estimate"}
                ]'::jsonb
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Workflow: create-work-item (single work item) ─────────────────────────
        // 5 stages: extract → preview → confirm → submit → result
        // preserveContext=true so prior conversation pre-fills the extract stage.
        await conn.ExecuteAsync("""
            INSERT INTO prompts (id, domain, description, context_tags, system_prompt, stages, pii_check, sample_prompt, preserve_context)
            VALUES (
                'create-work-item',
                'ado',
                'Create a single Azure DevOps work item (Epic, Feature, PBI, Bug, or Task). Triggered when the user asks to create one specific work item.',
                ARRAY[
                    'create work item','create a work item','create epic','create an epic',
                    'create feature','create a feature','create pbi','create a pbi',
                    'create product backlog item','create bug','create a bug',
                    'create task','create a task','new work item','new epic',
                    'new feature','new pbi','add work item','add epic',
                    'add feature','add pbi','add a work item'
                ],
                'You are an Azure DevOps work item creation assistant. Help users create clear, well-structured work items. Ask for missing required information (type and title). For area path and iteration path, ask the user if not provided — do not guess. For descriptions and acceptance criteria, generate professional HTML-formatted content based on the context.',
                $$[
                  {
                    "stageId": "extract",
                    "type": "ai-extract",
                    "required": ["type", "title"],
                    "clarifyPrompt": "I need a few details to create the work item. What type of work item is this (Epic, Feature, PBI, Bug, or Task) and what should the title be?",
                    "prompt": "Extract work item details from the conversation and the user''s latest message. Use all prior context to infer as much as possible. Return ONLY a raw JSON object — no markdown, no explanations. Must start with { and end with }.\n\nFields:\n- type (string, REQUIRED): Exactly one of: ''Epic'', ''Feature'', ''Product Backlog Item'', ''Bug'', ''Task''. Infer from context (e.g. ''epic'' → ''Epic'', ''pbi'' or ''story'' → ''Product Backlog Item''). Return null only if truly undeterminable.\n- title (string, REQUIRED): Concise title. Return null if not determinable.\n- description (string): HTML-formatted description appropriate for the work item type. For PBIs: User Story format (As a / I want / So that) followed by Background. For Epics: scope summary using <p> and <ul>. For Features: brief paragraph. Generate from context if enough information exists, otherwise null.\n- acceptanceCriteria (string): HTML-formatted BDD acceptance criteria for PBIs. Use <strong>Given</strong>/<strong>When</strong>/<strong>Then</strong>. Generate from context if possible, otherwise null.\n- areaPath (string): Area path if mentioned (e.g. ''CDS\\\\The Wizards''). Return null if not mentioned — do NOT guess.\n- iterationPath (string): Iteration/sprint path if mentioned. Return null if not mentioned — do NOT guess.\n- parentId (number): Numeric parent work item ID if mentioned (e.g. ''under Epic 123'' → 123). Return null if not mentioned.\n- priority (number): 1 (Critical), 2 (High), 3 (Medium), 4 (Low). Default null if not specified.\n- assignedTo (string): Assignee name or email if mentioned. Return null if not mentioned.\n- tags (string): Comma-separated tags if mentioned. Return null if not mentioned.\n- effort (number): Story points if mentioned. Return null if not mentioned.\n\nIMPORTANT: Return ONLY the JSON object.",
                    "confirmEach": false
                  },
                  {
                    "stageId": "preview",
                    "type": "ai-generate",
                    "prompt": "Present a clear preview of the work item to the user before it is created:\n\n**Work Item Preview**\n- **Type**: {{type}}\n- **Title**: {{title}}\n- **Description**: {{description}}\n- **Acceptance Criteria**: {{acceptanceCriteria}}\n- **Area Path**: {{areaPath}}\n- **Iteration**: {{iterationPath}}\n- **Parent ID**: {{parentId}}\n- **Priority**: {{priority}}\n- **Assigned To**: {{assignedTo}}\n- **Tags**: {{tags}}\n- **Effort**: {{effort}}\n\nOmit any fields that are null or empty. Tell the user to reply ''yes'' to create the work item, or describe any changes they want to make.",
                    "confirmEach": false
                  },
                  {
                    "stageId": "confirm",
                    "type": "confirmation",
                    "prompt": "Create this work item in Azure DevOps?",
                    "rewindTo": "extract",
                    "confirmEach": false
                  },
                  {
                    "stageId": "submit",
                    "type": "tool",
                    "toolId": "create-ado-work-item",
                    "parameters": {
                      "type": "{{type}}",
                      "title": "{{title}}",
                      "description": "{{description}}",
                      "acceptanceCriteria": "{{acceptanceCriteria}}",
                      "areaPath": "{{areaPath}}",
                      "iterationPath": "{{iterationPath}}",
                      "parentId": "{{parentId}}",
                      "priority": "{{priority}}",
                      "assignedTo": "{{assignedTo}}",
                      "tags": "{{tags}}",
                      "effort": "{{effort}}"
                    },
                    "confirmEach": false
                  },
                  {
                    "stageId": "result",
                    "type": "ai-analyze",
                    "prompt": "The work item was successfully created in Azure DevOps. Present a friendly confirmation showing the key details. Include the work item ID (from the tool result), type, title, and a link if the URL is available. Keep it concise.",
                    "dataFrom": ["submit"],
                    "confirmEach": false
                  }
                ]$$::jsonb,
                false,
                'Create a work item',
                true
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Workflow: decompose-prd (PRD → Epic + Features + PBIs hierarchy) ───────
        // 6 stages: extract → plan-preview → confirm-plan → create-epic →
        //           create-features (foreach) → result
        //
        // FLAT extract schema: epicTitle + epicDescription + features[] at top level.
        // AiExtractStageHandler stores only TOP-LEVEL keys in ctx.Fields — nested dot-
        // path tokens (e.g. {{hierarchy.epic.title}}) never resolve because there is no
        // recursive flattening in ai-extract. Using flat top-level fields fixes this.
        //
        // Token flow:
        //   ai-extract   → ctx.Fields["epicTitle"], ctx.Fields["epicDescription"],
        //                   ctx.Fields["features"] (raw JSON array string)
        //   create-epic  → {{epicTitle}}, {{epicDescription}} ✅ (top-level flat keys)
        //                   outputMap: { epicId: "id" } → ctx.Fields["epicId"] ✅
        //   foreach features (itemsFrom:"features") → FlattenInto writes feature.title,
        //                   feature.description, feature.pbis (raw JSON) ✅
        //   create-feature → {{feature.title}}, {{feature.description}}, {{epicId}} ✅
        //                   outputMap: { featureId: "id" } → ctx.Fields["featureId"] ✅
        //   foreach pbis (itemsFrom:"feature.pbis") → FlattenInto writes pbi.title,
        //                   pbi.description, pbi.acceptanceCriteria, pbi.effort ✅
        //   create-pbi   → {{pbi.*}}, {{featureId}} ✅
        await conn.ExecuteAsync("""
            INSERT INTO prompts (id, domain, description, context_tags, system_prompt, stages, pii_check, sample_prompt, preserve_context)
            VALUES (
                'decompose-prd',
                'ado',
                'Decompose a Product Requirements Document (PRD) into an Azure DevOps work item hierarchy: one Epic, multiple Features, and PBIs under each Feature. Triggered when the user pastes a PRD, requirements document, problem statement, functional spec, or any structured product specification.',
                ARRAY[
                    'decompose prd','break down prd','prd to work items','create work items from prd',
                    'convert prd','prd breakdown','decompose requirements','create hierarchy',
                    'create epic features pbis','break down requirements','import prd',
                    'create work items from requirements','prd decomposition',
                    'product requirements document','requirements document','functional requirements',
                    'executive summary','problem statement','solution overview','user roles',
                    'use cases','acceptance criteria','non-functional requirements','functional spec',
                    'product spec','feature specification','software requirements','project requirements',
                    'user stories from requirements','backlog from prd','plan from requirements'
                ],
                'You are an Azure DevOps planning assistant. You help teams decompose Product Requirements Documents and specification documents into structured work item hierarchies in Azure DevOps. You create well-organized Epics, Features, and Product Backlog Items with clear titles, HTML-formatted descriptions, and meaningful acceptance criteria derived directly from the document content.',
                $$[
                  {
                    "stageId": "extract",
                    "type": "ai-extract",
                    "required": ["epicTitle", "features"],
                    "clarifyPrompt": "I need a PRD or requirements document to decompose. Please paste the document content.",
                    "prompt": "Analyze the PRD or requirements document from the conversation and produce a flat JSON breakdown for Azure DevOps work item creation. Use ALL content in the conversation — the document may have been pasted earlier. Return ONLY a raw JSON object — no markdown, no explanations. Must start with { and end with }.\n\nFields (ALL TOP-LEVEL — do NOT nest under a ''hierarchy'' key):\n- epicTitle (string, REQUIRED): A concise Epic title summarizing the entire document scope. E.g. ''CoreLab CRF Data Uploader''. Never null.\n- epicDescription (string): HTML-formatted Epic description. Use <p> for paragraphs, <ul>/<li> for scope items. 2-4 sentences max summarizing what this Epic covers. Generate from the executive summary / solution overview.\n- features (array, REQUIRED): Array of Feature objects, one per major section, theme, or capability area. Each Feature object has:\n  - title (string): Feature title (concise, actionable, e.g. ''CRF File Upload and Validation'')\n  - description (string): HTML-formatted Feature description. 1-2 sentences using <p> tags.\n  - pbis (array): Array of PBI objects under this Feature, one per user story or requirement. Each PBI has:\n    - title (string): PBI title in user story format (''As a <user>, I want <capability>'' or concise requirement phrase)\n    - description (string): HTML user story body. As a / I want / So that using <p> and <ul>. Draw from the requirement text.\n    - acceptanceCriteria (string): HTML BDD acceptance criteria. Use <strong>Given</strong>/<strong>When</strong>/<strong>Then</strong> format. Derive from the acceptance criteria or requirement text.\n    - effort (number): Story point estimate (1, 2, 3, 5, 8, 13). Estimate based on complexity. Use null only if completely uninferable.\n- areaPath (string): ADO area path if explicitly mentioned. Return empty string '''' if not mentioned — do NOT guess.\n- iterationPath (string): ADO iteration path if explicitly mentioned. Return empty string '''' if not mentioned — do NOT guess.\n\nGuidelines:\n- Create one Feature per major PRD section or capability (e.g. Upload, Email Notifications, Upload History, Search).\n- Create 2-6 PBIs per Feature from the functional requirements, use cases, or user stories in that section.\n- Derive PBI titles and descriptions directly from the document — do not invent requirements.\n- ALL description and acceptanceCriteria fields MUST be valid HTML — use tags, not markdown or plaintext.\n- epicTitle and all feature/pbi titles must be specific and actionable, not generic.\n\nIMPORTANT: Return ONLY the flat JSON object. Do NOT wrap fields inside a ''hierarchy'' key.",
                    "confirmEach": false
                  },
                  {
                    "stageId": "plan-preview",
                    "type": "ai-generate",
                    "prompt": "Present the PRD decomposition plan to the user as a clear, readable hierarchy tree. The extracted data is available in context. Use epicTitle for the Epic name. Show the Epic, each Feature under it (with feature titles), and the PBIs under each Feature (with pbi titles). Format as:\n\n**Epic**: [epicTitle]\n  - **Feature**: [feature title]\n    - [pbi title]\n    - [pbi title]\n  - **Feature**: [feature title]\n    - [pbi title]\n    ...\n\nIf areaPath or iterationPath are set (non-empty), show them. If not set, omit them.\n\nAfter the tree, tell the user: ''Reply **yes** to create all these work items in Azure DevOps, or describe any changes you''d like to make first (e.g. rename a feature, add or remove PBIs, set an area path).''",
                    "confirmEach": false
                  },
                  {
                    "stageId": "confirm-plan",
                    "type": "confirmation",
                    "prompt": "Create all work items in Azure DevOps as shown above?",
                    "rewindTo": "extract",
                    "confirmEach": false
                  },
                  {
                    "stageId": "create-epic",
                    "type": "tool",
                    "toolId": "create-ado-work-item",
                    "parameters": {
                      "type": "Epic",
                      "title": "{{epicTitle}}",
                      "description": "{{epicDescription}}",
                      "areaPath": "{{areaPath}}",
                      "iterationPath": "{{iterationPath}}"
                    },
                    "outputMap": {
                      "epicId": "id"
                    },
                    "confirmEach": false
                  },
                  {
                    "stageId": "create-features",
                    "type": "foreach",
                    "itemsFrom": "features",
                    "itemAlias": "feature",
                    "confirmEach": false,
                    "stages": [
                      {
                        "stageId": "create-feature",
                        "type": "tool",
                        "toolId": "create-ado-work-item",
                        "parameters": {
                          "type": "Feature",
                          "title": "{{feature.title}}",
                          "description": "{{feature.description}}",
                          "parentId": "{{epicId}}",
                          "areaPath": "{{areaPath}}",
                          "iterationPath": "{{iterationPath}}"
                        },
                        "outputMap": {
                          "featureId": "id"
                        },
                        "confirmEach": false
                      },
                      {
                        "stageId": "create-pbis",
                        "type": "foreach",
                        "itemsFrom": "feature.pbis",
                        "itemAlias": "pbi",
                        "confirmEach": false,
                        "stages": [
                          {
                            "stageId": "create-pbi",
                            "type": "tool",
                            "toolId": "create-ado-work-item",
                            "parameters": {
                              "type": "Product Backlog Item",
                              "title": "{{pbi.title}}",
                              "description": "{{pbi.description}}",
                              "acceptanceCriteria": "{{pbi.acceptanceCriteria}}",
                              "effort": "{{pbi.effort}}",
                              "parentId": "{{featureId}}",
                              "areaPath": "{{areaPath}}",
                              "iterationPath": "{{iterationPath}}"
                            },
                            "confirmEach": false
                          }
                        ]
                      }
                    ]
                  },
                  {
                    "stageId": "result",
                    "type": "ai-analyze",
                    "prompt": "All work items have been created in Azure DevOps. Present a friendly summary of what was created. Show the Epic ID and title, then list each Feature with its ID and the count of PBIs created under it. If IDs are visible in the results, include them. Congratulate the user on having their PRD decomposed into actionable Azure DevOps work items.",
                    "dataFrom": ["create-epic","create-features"],
                    "confirmEach": false
                  }
                ]$$::jsonb,
                false,
                'Decompose my PRD into work items',
                true
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        // ── Workflow: attach-to-existing (attach children to an existing work item) ──
        // 7 stages: resolve-parent-input → validate-parent → plan-children →
        //           plan-preview → confirm-plan → create-test-cases (foreach) → result
        await conn.ExecuteAsync("""
            INSERT INTO prompts (id, domain, description, context_tags, system_prompt, stages, pii_check, sample_prompt, preserve_context)
            VALUES (
                'attach-to-existing',
                'ado',
                'Attach new work items (Features, PBIs, or Test Cases) to an existing Azure DevOps work item. Use when the user wants to add children to an already-existing Epic, Feature, or PBI by ID or search.',
                ARRAY[
                    'attach to existing','add to work item','add test cases','add pbis to feature',
                    'add features to epic','attach work items','link to existing','add children to',
                    'create under','add to #','existing work item','add test cases to pbi',
                    'add pbis','add features','attach to epic','attach to feature','attach to pbi',
                    'work item children','add under'
                ],
                'You are AVA acting as an Azure DevOps assistant. You help users attach new work items (Features, PBIs, and/or Test Cases) to existing items in their ADO hierarchy. You resolve the parent work item first (by ID or search), validate its type, then plan and create the appropriate children linked to it.',
                $$[
                  {
                    "stageId": "resolve-parent-input",
                    "type": "ai-extract",
                    "required": ["parentSearchQuery"],
                    "prompt": "Analyze the conversation to identify the existing Azure DevOps work item the user wants to attach children to. Return ONLY a raw JSON object.\n\nFields:\n- parentSearchQuery (string, REQUIRED): A WIQL query to find the parent work item. Construct it as follows:\n  - If the user gave a numeric ID (e.g. '#24889', 'PBI 4821', 'add to 4821'): SELECT [System.Id],[System.Title],[System.WorkItemType],[System.State] FROM WorkItems WHERE [System.Id] = <id>\n  - If the user gave a name AND a type (e.g. 'epic named test epic', 'under feature Login'): SELECT [System.Id],[System.Title],[System.WorkItemType],[System.State] FROM WorkItems WHERE [System.Title] = '<title>' AND [System.WorkItemType] = '<type>'\n  - If the user gave only a name without a clear type (e.g. 'under test epic', 'add to Login'): SELECT [System.Id],[System.Title],[System.WorkItemType],[System.State] FROM WorkItems WHERE [System.Title] = '<title>'\n- childrenDescription (string): The user's description of what children to create.\n\nExamples:\n- 'add test cases to PBI #4821' → parentSearchQuery: \"SELECT [System.Id],[System.Title],[System.WorkItemType],[System.State] FROM WorkItems WHERE [System.Id] = 4821\"\n- 'under the epic named test epic create a feature' → parentSearchQuery: \"SELECT [System.Id],[System.Title],[System.WorkItemType],[System.State] FROM WorkItems WHERE [System.Title] = 'test epic' AND [System.WorkItemType] = 'Epic'\"\n- 'create items under Login' → parentSearchQuery: \"SELECT [System.Id],[System.Title],[System.WorkItemType],[System.State] FROM WorkItems WHERE [System.Title] = 'Login'\"",
                    "clarifyPrompt": "Which existing Azure DevOps work item should I attach to? Please provide the name or ID (e.g. #12345 or 'epic named My Epic').",
                    "confirmEach": false
                  },
                  {
                    "stageId": "search-parent",
                    "type": "tool",
                    "toolId": "query_ado_work_items",
                    "parameters": {
                      "query": "{{parentSearchQuery}}"
                    },
                    "outputMap": {
                      "parentId": "parentId"
                    },
                    "confirmEach": false
                  },
                  {
                    "stageId": "pick-parent",
                    "type": "ai-extract",
                    "required": ["parentId"],
                    "dataFrom": ["search-parent"],
                    "prompt": "You are given the JSON result of an Azure DevOps work item search in the data below. Extract the parent work item's numeric ID from the 'workItems' array.\n\nRules:\n- If 'workItems' contains exactly ONE item, return that item's 'id' as parentId. THIS IS THE COMMON CASE.\n- If 'workItems' contains MULTIPLE items, pick the one whose title and type best match what the user asked for. If it is genuinely ambiguous, return an empty object {}.\n- If 'workItems' is empty or missing, return an empty object {}.\n\nReturn ONLY a raw JSON object and nothing else. Example: {\"parentId\": 24889}",
                    "clarifyPrompt": "I found multiple work items matching that name. Please specify which one by ID (e.g. #12345), or provide more detail about which one you mean.",
                    "confirmEach": false
                  },
                  {
                    "stageId": "validate-parent",
                    "type": "tool",
                    "toolId": "ado-get-work-item",
                    "parameters": {
                      "id": "{{parentId}}"
                    },
                    "outputMap": {
                      "parentType": "type",
                      "parentTitle": "title",
                      "parentAreaPath": "areaPath",
                      "parentIterationPath": "iterationPath"
                    },
                    "confirmEach": false
                  },
                  {
                    "stageId": "confirm-parent",
                    "type": "confirmation",
                    "prompt": "I found **{{parentType}} #{{parentId}}** — '{{parentTitle}}'. Should I proceed to create children under this work item?"
                  },
                  {
                    "stageId": "plan-children",
                    "type": "ai-extract",
                    "required": ["planType"],
                    "prompt": "You are planning the child work items to create under an existing parent work item (type: {{parentType}}, title: '{{parentTitle}}'). Return ONLY a raw JSON object.\n\nCRITICAL - CREATE EXACTLY WHAT WAS REQUESTED:\n- Create ONLY the items the user explicitly asked for - no more, no less. Do NOT invent, pad, duplicate, or add any extra items, examples, or placeholders.\n- Use the EXACT titles the user provided (e.g. if they say a pbi 'Test PBI', the title is 'Test PBI').\n- Create multiple items ONLY when the user explicitly asks for multiple.\n- Never create the same item as two different types. Each requested item maps to exactly one work item of the type the user named.\n\nYou MUST include a 'planType' field set to the DOMINANT child type requested: 'features', 'pbis', or 'test-cases'.\n\nReturn any combination of these top-level arrays, each populated ONLY with items the user wants attached DIRECTLY under the parent. Omit or leave empty any array not requested:\n\n- features (array): direct child Features of the parent. Each has:\n     - title (string): the Feature title\n     - description (string): HTML feature description\n     - pbis (array): include ONLY if the user wants PBIs nested UNDER this new feature. Each has title, description (HTML), acceptanceCriteria (HTML BDD Given/When/Then), effort (number)\n\n- pbis (array): direct child Product Backlog Items of the parent. Each has:\n     - title (string): the PBI/user story title\n     - description (string): HTML description\n     - acceptanceCriteria (string): HTML acceptance criteria in BDD Given/When/Then format\n     - effort (number): story points estimate\n     - testCases (array): include ONLY if requested. Each has title, steps [{action, expectedResult}], priority\n\n- testCases (array): direct child Test Cases of the parent. Each has:\n     - title (string): descriptive test case title\n     - steps (array): array of {action, expectedResult} objects\n     - priority (number): 1=Critical, 2=High, 3=Medium, 4=Low\n\nPLACEMENT RULES:\n- Put an item in a TOP-LEVEL array when the user wants it directly under {{parentType}} '{{parentTitle}}'.\n- Nest a PBI inside a feature's 'pbis' array ONLY when the user explicitly says that PBI belongs under that new feature.\n- Honor mixed requests: e.g. 'under this epic create a feature X and a pbi Y' returns BOTH features:[{title:'X'...}] AND pbis:[{title:'Y'...}] as direct children.\n- If the user is vague about type, default by parent: Epic->features, Feature->pbis, Product Backlog Item->test-cases. But ALWAYS honor explicitly named types over the default.\n\nGenerate realistic descriptions and acceptance criteria for the requested items, but NEVER add items that were not requested.",
                    "clarifyPrompt": "What specific children would you like to add? Describe the test scenarios, user stories, or features you need.",
                    "confirmEach": false
                  },
                  {
                    "stageId": "plan-preview",
                    "type": "ai-generate",
                    "prompt": "Present the plan as a clear tree showing what will be attached under the existing parent:\n\n**Attaching under [{{parentType}} #{{parentId}}] '{{parentTitle}}':**\n\nList each item with bullet points showing the hierarchy. For PBIs show their test cases indented. For Features show their PBIs indented.\n\nEnd with: 'Reply **yes** to create these in Azure DevOps, or describe any changes.'",
                    "rewindTo": "plan-children",
                    "confirmEach": false
                  },
                  {
                    "stageId": "create-test-cases",
                    "type": "foreach",
                    "itemsFrom": "testCases",
                    "itemAlias": "tc",
                    "confirmEach": false,
                    "stages": [
                      {
                        "stageId": "create-tc",
                        "type": "tool",
                        "toolId": "ado-create-test-case",
                        "parameters": {
                          "title": "{{tc.title}}",
                          "steps": "{{tc.steps}}",
                          "testedByIds": "[{{parentId}}]",
                          "priority": "{{tc.priority}}",
                          "areaPath": "{{parentAreaPath}}",
                          "iterationPath": "{{parentIterationPath}}"
                        },
                        "confirmEach": false
                      }
                    ]
                  },
                  {
                    "stageId": "create-pbis",
                    "type": "foreach",
                    "itemsFrom": "pbis",
                    "itemAlias": "pbi",
                    "confirmEach": false,
                    "stages": [
                      {
                        "stageId": "create-pbi",
                        "type": "tool",
                        "toolId": "ado-mcp-server-create-work-item",
                        "parameters": {
                          "type": "Product Backlog Item",
                          "title": "{{pbi.title}}",
                          "description": "{{pbi.description}}",
                          "acceptanceCriteria": "{{pbi.acceptanceCriteria}}",
                          "effort": "{{pbi.effort}}",
                          "parentId": "{{parentId}}",
                          "areaPath": "{{parentAreaPath}}",
                          "iterationPath": "{{parentIterationPath}}"
                        },
                        "outputMap": { "pbiId": "id" },
                        "confirmEach": false
                      },
                      {
                        "stageId": "create-pbi-tests",
                        "type": "foreach",
                        "itemsFrom": "pbi.testCases",
                        "itemAlias": "tc",
                        "confirmEach": false,
                        "stages": [
                          {
                            "stageId": "create-tc",
                            "type": "tool",
                            "toolId": "ado-create-test-case",
                            "parameters": {
                              "title": "{{tc.title}}",
                              "steps": "{{tc.steps}}",
                              "testedByIds": "[{{pbiId}}]",
                              "priority": "{{tc.priority}}",
                              "areaPath": "{{parentAreaPath}}",
                              "iterationPath": "{{parentIterationPath}}"
                            },
                            "confirmEach": false
                          }
                        ]
                      }
                    ]
                  },
                  {
                    "stageId": "create-features",
                    "type": "foreach",
                    "itemsFrom": "features",
                    "itemAlias": "feat",
                    "confirmEach": false,
                    "stages": [
                      {
                        "stageId": "create-feat",
                        "type": "tool",
                        "toolId": "ado-mcp-server-create-work-item",
                        "parameters": {
                          "type": "Feature",
                          "title": "{{feat.title}}",
                          "description": "{{feat.description}}",
                          "parentId": "{{parentId}}",
                          "areaPath": "{{parentAreaPath}}",
                          "iterationPath": "{{parentIterationPath}}",
                          "additionalFields": "{\"Custom.Rel_Purpose\":\"Enhancement\",\"Custom.Source\":\"Systems Project Request\"}"
                        },
                        "outputMap": { "featureId": "id" },
                        "confirmEach": false
                      },
                      {
                        "stageId": "create-feat-pbis",
                        "type": "foreach",
                        "itemsFrom": "feat.pbis",
                        "itemAlias": "pbi",
                        "confirmEach": false,
                        "stages": [
                          {
                            "stageId": "create-pbi",
                            "type": "tool",
                            "toolId": "ado-mcp-server-create-work-item",
                            "parameters": {
                              "type": "Product Backlog Item",
                              "title": "{{pbi.title}}",
                              "description": "{{pbi.description}}",
                              "acceptanceCriteria": "{{pbi.acceptanceCriteria}}",
                              "effort": "{{pbi.effort}}",
                              "parentId": "{{featureId}}",
                              "areaPath": "{{parentAreaPath}}",
                              "iterationPath": "{{parentIterationPath}}"
                            },
                            "confirmEach": false
                          }
                        ]
                      }
                    ]
                  },
                  {
                    "stageId": "result",
                    "type": "ai-analyze",
                    "prompt": "All work items have been created and linked to {{parentType}} #{{parentId}} '{{parentTitle}}'. Present a friendly summary showing each created item with its new ID, type, title, and link relationship (Parent-Child or Tested By). Congratulate the user.",
                    "dataFrom": ["create-test-cases", "create-pbis", "create-features", "validate-parent"],
                    "confirmEach": false
                  }
                ]$$::jsonb,
                false,
                'Add PBIs to Feature #4821',
                true
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        _logger.LogInformation("ADO domain, tool, and workflows seeded successfully.");
    }

}
