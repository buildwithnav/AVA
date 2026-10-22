using System.Text.Json;
using Ava.Api.Models;

namespace Ava.Api.Config;

// ── App settings ──────────────────────────────────────────────────────────────

public class AvaSettings
{
    public AzureOpenAiSettings AzureOpenAi { get; set; } = new();
    public GitHubCopilotSettings GitHubCopilot { get; set; } = new();
    public Dictionary<string, ServiceEndpointSettings> Services { get; set; } = new();
    public KnowledgeBaseSettings KnowledgeBase { get; set; } = new();
    public ConversationSettings Conversation { get; set; } = new();
    public DomainFilesSettings DomainFiles { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();

    /// <summary>
    /// Per-model pricing for cost estimation. Keys match the resolved model ID
    /// (e.g. "gpt-4.1", "gpt-4o-mini", "claude-sonnet-4.6").
    /// Prices are in USD per million tokens. If a model is not listed, cost = 0.
    /// </summary>
    public Dictionary<string, ModelPricing> ModelPricing { get; set; } = new();
}

public class AzureOpenAiSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = "gpt-4";
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Maps logical model names (used in PromptDefinition.ModelId) to Azure deployment names.
    /// Example: { "gpt-4o-mini": "gpt-4o-mini-deployment", "gpt-4.1": "gpt-4.1-deployment" }.
    /// If a modelId is not in this map, the ChatDeployment fallback is used.
    /// </summary>
    public Dictionary<string, string> ModelMappings { get; set; } = new();
}

public class GitHubCopilotSettings
{
    public string ClientId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://api.githubcopilot.com";
    public string DefaultChatModel { get; set; } = "azure-openai/gpt-4.1";
    public List<ModelOption> AvailableModels { get; set; } = new();

    /// <summary>
    /// Default LLM temperature applied to all chat completion calls (0.0 = deterministic,
    /// 1.0 = model default, 2.0 = maximum randomness). Applied when a call does not
    /// already set Temperature explicitly (e.g. QueryRouterService which uses 0.0f).
    /// Only applied when the value differs from 1.0 to avoid unnecessary option objects.
    /// </summary>
    public float DefaultTemperature { get; set; } = 1.0f;
}

public class ModelOption
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
}

/// <summary>USD pricing per million tokens for a model. Used to estimate run cost.</summary>
public class ModelPricing
{
    /// <summary>Cost per million input (prompt) tokens in USD.</summary>
    public decimal InputPricePerMillionTokens { get; set; }

    /// <summary>Cost per million output (completion) tokens in USD.</summary>
    public decimal OutputPricePerMillionTokens { get; set; }
}

public class ServiceEndpointSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>When true, outgoing requests use the CDS cookie-based auth from AuthSettings.</summary>
    public bool RequiresCdsAuth { get; set; }

    /// <summary>
    /// When true, this service speaks the MCP Streamable HTTP protocol (JSON-RPC).
    /// McpClientService handles all communication; ToolExecutorService routes tool
    /// calls with mcp:-prefixed routes to this service automatically.
    /// </summary>
    public bool IsMcp { get; set; }

    /// <summary>
    /// Optional prefix that this MCP server's gateway prepends to tool names
    /// (e.g. "context-forge-server-" if the gateway exposes tools as
    /// "context-forge-server-get_work_items"). Used by McpClientService to build
    /// canonical name aliases for transparent name resolution. Leave empty for
    /// direct MCP servers that return bare tool names.
    /// </summary>
    public string? ToolPrefix { get; set; }

    /// <summary>
    /// Optional bearer token for the endpoint (e.g. a Context Forge MCP gateway).
    /// When set, the MCP client authenticates to the gateway with "Authorization: Bearer {token}".
    /// This is additive — the per-user X-ADO-PAT is still sent so the gateway can
    /// forward ADO credentials to the upstream server. Keep out of source control — supply via
    /// env var AVA__Services__context-forge__BearerToken or appsettings.Secrets.json.
    /// </summary>
    public string? BearerToken { get; set; }
}

public class AuthSettings
{
    public string TokenUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string GrantType { get; set; } = "password";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool SetCookie { get; set; } = true;

    /// <summary>
    /// When true, forward cas-at/cas-rt cookies from incoming requests to downstream CDS services.
    /// When false, use password-based authentication (default for local development).
    /// </summary>
    public bool UseCookieForwarding { get; set; } = false;
}

/// <summary>
/// Module-access configuration. When <see cref="Enabled"/> is false (local development)
/// the gateway is never called and <see cref="LocalModules"/> is returned instead — the
/// server-side analogue of the Angular template's LOCAL_MODULES.
/// </summary>
public class SecuritySettings
{
    /// <summary>Master switch. False = local dev mode: LocalModules + LocalUserId, no gateway calls.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>AVA's application key registered with the CDO Gateway.</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>Gateway base URL, e.g. https://cdstest.abbott.com/core/api/.</summary>
    public string GatewayBaseUrl { get; set; } = string.Empty;

    /// <summary>Identity used when Enabled is false, so local dev has a stable user for grants.</summary>
    public string LocalUserId { get; set; } = "local.user";

    /// <summary>Display name paired with LocalUserId.</summary>
    public string LocalUserName { get; set; } = "Local Developer";

    /// <summary>
    /// How to treat a domain with no module_key. True (default) grants access to everyone —
    /// use during rollout. Set false once every domain has a registered key.
    /// </summary>
    public bool AllowUnmappedDomains { get; set; } = true;

    /// <summary>Seconds to cache a user's gateway module list. 0 disables caching.</summary>
    public int ModuleCacheSeconds { get; set; } = 300;

    /// <summary>Grants used when Enabled is false. Flip flags here to exercise denial paths locally.</summary>
    public List<Module> LocalModules { get; set; } = new();

    /// <summary>
    /// When Enabled is false, grants the local user every child domain without needing rows in
    /// domain_grants. Set false to test child-domain denial against real grant data.
    /// </summary>
    public bool LocalGrantAllSubDomains { get; set; } = true;
}

public class KnowledgeBaseSettings
{
    public string KBServiceBaseUrl { get; set; } = "http://localhost:8100";
    public string ApiKey { get; set; } = string.Empty;
    public int IdleTimeoutMinutes { get; set; } = 60;
    public int MaxToolIterations { get; set; } = 5;
    public int MaxFileSizeMB { get; set; } = 50;

    /// <summary>
    /// Number of recent conversation turns carried into a workflow when the matched workflow
    /// has PreserveContext = true. Keeps token usage bounded. 0 or negative falls back to 10.
    /// </summary>
    public int MaxContextTurns { get; set; } = 10;
}

public class ConversationSettings
{
    public int CompactionThreshold { get; set; } = 14;
    public int RetainRecentMessages { get; set; } = 6;
    public string CompactionModel { get; set; } = "azure-openai/gpt-4o-mini";
    public int MaxSnapshots { get; set; } = 5;
}

public class DomainFilesSettings
{
    public int MaxFileSizeMB { get; set; } = 50;
    public int ChunkSize { get; set; } = 1000;
    public int ChunkOverlap { get; set; } = 125;
    public int AutoRetrieveTopK { get; set; } = 5;
    public float SimilarityThreshold { get; set; } = 0.55f;
    public int MaxAutoRetrieveTokens { get; set; } = 4000;
    public List<string> SupportedExtensions { get; set; } = new() { ".txt", ".md", ".csv", ".json", ".xlsx" };
    public RetrievalSettings Retrieval { get; set; } = new();
}

public class RetrievalSettings
{
    // Chunking
    public int ParentChunkMaxChars { get; set; } = 2000;
    public int ChildChunkSize { get; set; } = 500;
    public int ChildChunkOverlap { get; set; } = 50;

    // Fast strategy
    public int FastTopK { get; set; } = 10;

    // Expanded strategy
    public int ExpandedRetrieveK { get; set; } = 50;
    public int ExpandedReturnK { get; set; } = 15;

    // Agentic strategy
    public int AgenticMaxIterations { get; set; } = 3;
    public int AgenticTokenBudget { get; set; } = 8000;

    // Router
    public double RouterConfidenceThreshold { get; set; } = 0.85;
    public string RouterModel { get; set; } = "azure-openai/gpt-4o-mini";
}

// ── User session (in-memory; Redis-swappable in Phase 5) ──────────────────────

public class UserSession
{
    /// <summary>GitHub OAuth token — used to call GitHub Models inference API.</summary>
    public string? GitHubToken { get; set; }

    /// <summary>Azure DevOps Personal Access Token — used for ADO tool calls.</summary>
    public string? AdoPat { get; set; }

    /// <summary>GitHub Models model ID selected by the user (e.g. "azure-openai/gpt-4.1").</summary>
    public string? SelectedModel { get; set; }

    /// <summary>Currently focused KB for /kb mode.</summary>
    public string? ActiveKnowledgeBaseId { get; set; }

    /// <summary>Session-scoped KBs (from uploads or storeAsKB tool outputs).</summary>
    public List<string> SessionKnowledgeBases { get; set; } = new();
}

// ── Repository interfaces (swap JSON → Oracle without touching engine) ────────

public interface IPromptRepository
{
    Task<List<PromptDefinition>> GetByDomainAsync(string domain);
    Task<PromptDefinition?> GetByIdAsync(string id);
    Task<List<PromptDefinition>> GetAllAsync();
    Task<List<PromptDefinition>> GetArchivedAsync();
    Task<PromptDefinition> CreateAsync(PromptDefinition prompt);
    Task<PromptDefinition?> UpdateAsync(string id, PromptDefinition prompt);
    Task ArchiveAsync(string id);
    Task RestoreAsync(string id);
}

public interface IToolRepository
{
    Task<List<ToolDefinition>> GetAllAsync();
    Task<List<ToolDefinition>> GetArchivedAsync();
    Task<ToolDefinition?> GetByIdAsync(string id);
    Task<ToolDefinition> CreateAsync(ToolDefinition tool);
    Task<ToolDefinition> UpdateAsync(ToolDefinition tool);
    Task ArchiveAsync(string id);
    Task RestoreAsync(string id);
}

public interface ITemplateRepository
{
    Task<List<TemplateDefinition>> GetAllAsync();
    Task<List<TemplateDefinition>> GetArchivedAsync();
    Task<TemplateDefinition?> GetByIdAsync(string id);
    void Add(TemplateDefinition template);
    bool Update(TemplateDefinition template);
    Task ArchiveAsync(string id);
    Task RestoreAsync(string id);
}

public interface IDomainRepository
{
    /// <summary>Top-level domains only (parent_id IS NULL).</summary>
    Task<List<DomainDefinition>> GetAllAsync();
    /// <summary>Top-level domains only (parent_id IS NULL), including archived.</summary>
    Task<List<DomainDefinition>> GetAllIncludingArchivedAsync();
    /// <summary>Any domain by id — top-level or child.</summary>
    Task<DomainDefinition?> GetByIdAsync(string id);
    Task AddAsync(DomainDefinition domain);
    Task<bool> UpdateAsync(DomainDefinition domain);
    Task<bool> ArchiveAsync(string id);
    Task<bool> RestoreAsync(string id);
    /// <summary>Hard delete — also removes the domain's grants. Children only; prefer ArchiveAsync for top-level domains.</summary>
    Task<bool> DeleteAsync(string id);

    /// <summary>Children of a domain (2-tier cap: children never have children of their own).</summary>
    Task<List<DomainDefinition>> GetChildrenAsync(string parentId, bool includeArchived = false);

    // ── Grants ──────────────────────────────────────────────────────────────
    // Folded in here rather than a separate repository: a grant is meaningless without its
    // parent/child domains, and every caller needs both together.

    /// <summary>All grants for a principal, including any wildcard rows.</summary>
    Task<List<DomainGrant>> GetGrantsForPrincipalAsync(string principalId, IEnumerable<string> groupIds);
    Task<List<DomainGrant>> GetGrantsByParentAsync(string parentId);
    Task UpsertGrantAsync(DomainGrant grant);
    Task<bool> RemoveGrantAsync(string id);
}

public interface IKnowledgeBaseRepository
{
    Task<List<KnowledgeBase>> GetAllAsync();
    Task<KnowledgeBase?> GetByIdAsync(string id);
    Task<List<KnowledgeBase>> GetByDomainAsync(string domainId);
    void Add(KnowledgeBase kb);
    void Remove(string id);
    /// <summary>Persist updated metadata (rowCount, schema, columnDefinitions, lastRefreshedAt etc.) for domain KBs.</summary>
    Task UpdateAsync(KnowledgeBase kb);
    /// <summary>Awaited create-or-update for a domain KB — inserts if new, updates if it exists. Deterministic (unlike fire-and-forget Add).</summary>
    Task UpsertAsync(KnowledgeBase kb);
}

public interface IDomainFileRepository
{
    Task<List<DomainFile>> GetByDomainAsync(string domainId);
    Task<DomainFile?> GetByIdAsync(string id);
    Task AddAsync(DomainFile file);
    Task UpdateAsync(DomainFile file);
    Task RemoveAsync(string id);
}

// ── JSON implementations ──────────────────────────────────────────────────────

public class JsonKnowledgeBaseRepository : IKnowledgeBaseRepository
{
    private readonly List<KnowledgeBase> _knowledgeBases;
    private readonly object _lock = new();

    public JsonKnowledgeBaseRepository(List<KnowledgeBase> knowledgeBases)
        => _knowledgeBases = knowledgeBases;

    public Task<List<KnowledgeBase>> GetAllAsync()
    {
        lock (_lock) return Task.FromResult(_knowledgeBases.Where(k => k.Enabled).ToList());
    }

    public Task<KnowledgeBase?> GetByIdAsync(string id)
    {
        lock (_lock) return Task.FromResult(_knowledgeBases
            .FirstOrDefault(k => k.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && k.Enabled));
    }

    public Task<List<KnowledgeBase>> GetByDomainAsync(string domainId)
    {
        lock (_lock) return Task.FromResult(_knowledgeBases
            .Where(k => k.Enabled && k.Scope == "domain" &&
                        (k.DomainId ?? "").Equals(domainId, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    public void Add(KnowledgeBase kb)
    {
        lock (_lock)
        {
            _knowledgeBases.RemoveAll(k => k.Id == kb.Id);
            _knowledgeBases.Add(kb);
        }
    }

    public void Remove(string id)
    {
        lock (_lock) _knowledgeBases.RemoveAll(k => k.Id == id);
    }

    public Task UpdateAsync(KnowledgeBase kb)
    {
        lock (_lock)
        {
            var idx = _knowledgeBases.FindIndex(k => k.Id == kb.Id);
            if (idx >= 0) _knowledgeBases[idx] = kb;
        }
        return Task.CompletedTask;
    }

    public Task UpsertAsync(KnowledgeBase kb)
    {
        lock (_lock)
        {
            _knowledgeBases.RemoveAll(k => k.Id == kb.Id);
            _knowledgeBases.Add(kb);
        }
        return Task.CompletedTask;
    }
}

// ── knowledgebases.json loader ────────────────────────────────────────────────

public static class KnowledgeBasesConfig
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    public static List<KnowledgeBase> Load(string path)
    {
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<KnowledgeBase>>(json, _opts) ?? new();
    }
}
