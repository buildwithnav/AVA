using Ava.Api.Config;
using Ava.Api.Data;
using Ava.Api.Helpers;
using Ava.Api.Models;
using Ava.Api.Services;
using Ava.Api.Services.Security;
using Ava.Api.Services.Stages;
using Ava.Vectors;
using Ava.Vectors.Providers;
using Ava.Vectors.Stores;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── File logging ──────────────────────────────────────────────────────────────
var logDir = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Logging.AddProvider(new FileLoggerProvider(logDir));

// ── Configuration ─────────────────────────────────────────────────────────────
// Optional untracked secrets file (e.g. the ado-mcp gateway BearerToken). It is not in
// source control and is NOT produced by dotnet publish, so it survives redeploys. Values
// here override appsettings*.json. Place it next to the app DLLs on the server.
//
// TODO(deploy): The production server (R:\ava) uses ENVIRONMENT-SPECIFIC values in its own
// appsettings.json — ConnectionStrings:AvaDb (local "Cua122968q" DB) and ConnectionStrings:Redis
// (10.237.129.82). `dotnet publish` / publish.ps1 OVERWRITE appsettings.json and
// appsettings.Production.json, which will clobber those and break startup (500.30) — this bit us
// on 2026-06-04. Until fixed, either (a) preserve the server's appsettings.json/Production.json
// during deploy (like we already do for web.config), or (b) move AvaDb + Redis + the ado-mcp
// BaseUrl/BearerToken into appsettings.Secrets.json (loaded below, highest precedence) so the
// base files can be safely overwritten.
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: true);

builder.Services.Configure<AvaSettings>(builder.Configuration.GetSection("Ava"));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Authentication"));

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("AvaDb")
    ?? throw new InvalidOperationException("ConnectionStrings:AvaDb is required");

var dbFactory = new DbConnectionFactory(connectionString);
builder.Services.AddSingleton(dbFactory);
builder.Services.AddTransient<DatabaseMigrator>();

// ── Repository registrations (Postgres) ───────────────────────────────────────
builder.Services.AddSingleton<IPromptRepository>(sp => new PostgresPromptRepository(sp.GetRequiredService<DbConnectionFactory>()));
builder.Services.AddSingleton<IToolRepository>(sp => new PostgresToolRepository(sp.GetRequiredService<DbConnectionFactory>()));
builder.Services.AddSingleton<ITemplateRepository>(sp => new PostgresTemplateRepository(sp.GetRequiredService<DbConnectionFactory>()));
builder.Services.AddSingleton<IDomainRepository>(sp => new PostgresDomainRepository(sp.GetRequiredService<DbConnectionFactory>()));
builder.Services.AddSingleton<IDomainFileRepository>(sp => new PostgresDomainFileRepository(sp.GetRequiredService<DbConnectionFactory>()));

// Analytics + conversation archive repositories
builder.Services.AddSingleton<PostgresUsageRepository>();
builder.Services.AddSingleton<PostgresConversationRepository>();

// KnowledgeBase: hybrid Postgres (domain-scoped) + in-memory (session-scoped)
var contentRoot = builder.Environment.ContentRootPath;
builder.Services.AddSingleton<PostgresKnowledgeBaseRepository>(sp =>
    new PostgresKnowledgeBaseRepository(
        sp.GetRequiredService<DbConnectionFactory>(),
        sp.GetRequiredService<ILogger<PostgresKnowledgeBaseRepository>>()));
builder.Services.AddSingleton<IKnowledgeBaseRepository>(sp =>
    sp.GetRequiredService<PostgresKnowledgeBaseRepository>());

// ── Core service registrations ────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();  // Required for cookie forwarding
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("github-auth")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false });

// ── Security / module access ──────────────────────────────────────────────────
// Scoped, not singleton: identity and module grants are per-request.
builder.Services.AddHttpClient("cdo-gateway");
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<DomainAccessFilter>();

// ── Vector / Embedding services (Ava.Vectors) ────────────────────────────────
builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<AvaSettings>>().Value.AzureOpenAi;
    return new AzureOpenAiProvider(settings.Endpoint, settings.ApiKey, settings.EmbeddingDeployment);
});
builder.Services.AddSingleton<IVectorStore>(sp =>
{
    var db = sp.GetRequiredService<DbConnectionFactory>();
    return new PgVectorStore(async () => await db.OpenConnectionAsync());
});
builder.Services.AddSingleton<IVectorIndex<PromptDefinition>>(sp =>
    new VectorIndexService<PromptDefinition>(
        sp.GetRequiredService<IEmbeddingProvider>(),
        sp.GetRequiredService<IVectorStore>(),
        collection: "prompts",
        sp.GetRequiredService<ILogger<VectorIndexService<PromptDefinition>>>()));

// ── Redis / Session Store ─────────────────────────────────────────────────────
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    try
    {
        var redis = ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false,connectTimeout=5000,syncTimeout=3000,asyncTimeout=3000");
        if (!redis.IsConnected)
        {
            redis.Dispose();
            Console.WriteLine("[WARN] Redis connection could not be established. Falling back to InMemorySessionStore.");
            builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        }
        else
        {
            builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
            builder.Services.AddSingleton<ISessionStore, RedisSessionStore>();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Redis connection failed ({ex.Message}). Falling back to InMemorySessionStore.");
        builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
    }
}
else
{
    builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
}

builder.Services.AddSingleton<ToolRegistryService>();
builder.Services.AddSingleton<ConversationService>();
builder.Services.AddSingleton<AgentRunService>();
builder.Services.AddSingleton<SemanticSearchService>();
builder.Services.AddSingleton<UserSessionService>();
builder.Services.AddSingleton<GitHubAuthService>();
builder.Services.AddSingleton<IAuthService, AuthService>();
builder.Services.AddSingleton<ToolExecutorService>();
builder.Services.AddSingleton<McpClientService>();
builder.Services.AddSingleton<IPiiDetectionService, NullPiiDetectionService>();
builder.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();
builder.Services.AddSingleton<StreamingChatHelper>();
builder.Services.AddHttpClient<KnowledgeBaseService>();
builder.Services.AddSingleton<KbExportCacheService>();
builder.Services.AddSingleton<DomainFileService>();
builder.Services.AddSingleton<QueryRouterService>();
builder.Services.AddSingleton<CompactionService>();

// ── Stage handlers ────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IStageHandler, AiExtractStageHandler>();
builder.Services.AddSingleton<IStageHandler, ToolStageHandler>();
builder.Services.AddSingleton<IStageHandler, UserSelectStageHandler>();
builder.Services.AddSingleton<IStageHandler, AiGenerateStageHandler>();
builder.Services.AddSingleton<IStageHandler, AiAnalyzeStageHandler>();
builder.Services.AddSingleton<IStageHandler, ConfirmationStageHandler>();
builder.Services.AddSingleton<IStageHandler, ForEachStageHandler>();
builder.Services.AddSingleton<IStageHandler, AgenticStageHandler>();

// ── Orchestration ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddSingleton<WorkflowCreatorService>();

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
builder.Services.AddControllers(options =>
{
    // Blanket domain-access enforcement for any endpoint taking a domainId.
    options.Filters.AddService<DomainAccessFilter>();
});

builder.Services.AddCors(o => o.AddPolicy("AllowAll", p =>
    p.SetIsOriginAllowed(_ => true)
     .AllowAnyMethod().AllowAnyHeader().AllowCredentials()));

// ── Build app ─────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors("AllowAll");

// ── Pylon user tracking ───────────────────────────────────────────────────────
// Decode the (trusted, unvalidated) Pylon JWT on every request and stash the user
// identity in HttpContext.Items so controllers can record who made the request.
// Pylon sits in front of this API in real deployments, so the token is trusted and
// its signature is NOT verified — we only read the nameid/unique_name claims.
//
// LOCAL DEV: there is no Pylon gateway in front of the API when running locally, so
// requests carry no JWT. To keep local runs working, we inject a configurable dev
// user when running in the Development environment (or when Ava:LocalDev:BypassAuth
// is truthy). In non-Development environments no fallback is injected, so the
// user-tracked endpoints (chat, prompt/execute) return 401 without a valid JWT.
// See ava-api/README.md ("Local development — authentication") for details.
var localBypassAuth = app.Environment.IsDevelopment()
    || (app.Configuration["Ava:LocalDev:BypassAuth"] ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes";
var devUserId   = app.Configuration["Ava:LocalDev:UserId"]   ?? "0";
var devUsername = app.Configuration["Ava:LocalDev:Username"] ?? "local.dev";

app.Use(async (context, next) =>
{
    if (PylonUserExtensions.TryDecodePylonJwt(context.Request.Headers.Authorization, out var pylonUser))
    {
        context.Items[PylonUserExtensions.UserIdItemKey]   = pylonUser.UserId;
        context.Items[PylonUserExtensions.UsernameItemKey] = pylonUser.Username;
    }
    else if (localBypassAuth)
    {
        context.Items[PylonUserExtensions.UserIdItemKey]   = devUserId;
        context.Items[PylonUserExtensions.UsernameItemKey] = devUsername;
    }

    await next();
});

// ── Startup: run database migration (opt-in) ──────────────────────────────────
// Gated behind RUN_MIGRATIONS so normal startups never create schema or reseed the
// database. Enable when you explicitly want it, e.g. `RUN_MIGRATIONS=true ./start.sh`.
// Accepted truthy values: "true", "1", "yes" (case-insensitive).
var runMigrations = (app.Configuration["RUN_MIGRATIONS"] ?? "")
    .Trim().ToLowerInvariant() is "true" or "1" or "yes";

if (runMigrations)
{
    app.Logger.LogInformation("RUN_MIGRATIONS is set — running database migration and seed.");
    var migrator = app.Services.GetRequiredService<DatabaseMigrator>();
    await migrator.MigrateAsync();

    // Seed domain KBs from knowledgebases.json (idempotent — skips existing rows)
    var kbRepo = app.Services.GetRequiredService<PostgresKnowledgeBaseRepository>();
    var kbConfigPath = Path.Combine(contentRoot, "knowledgebases.json");
    var kbDefinitions = KnowledgeBasesConfig.Load(kbConfigPath);
    if (kbDefinitions.Count > 0)
        await kbRepo.SeedFromConfigAsync(kbDefinitions);
}
else
{
    app.Logger.LogInformation(
        "Skipping database migration/seed (RUN_MIGRATIONS not set). Set RUN_MIGRATIONS=true to run schema + seed.");
}

// Log what's in the database
var promptRepo = app.Services.GetRequiredService<IPromptRepository>();
var toolRepo = app.Services.GetRequiredService<IToolRepository>();
var templateRepo = app.Services.GetRequiredService<ITemplateRepository>();
var domainRepo = app.Services.GetRequiredService<IDomainRepository>();

var prompts = await promptRepo.GetAllAsync();
var tools = await toolRepo.GetAllAsync();
var templates = await templateRepo.GetAllAsync();
var domains = await domainRepo.GetAllAsync();

app.Logger.LogInformation(
    "Loaded {Prompts} prompts, {Tools} tools, {Templates} templates, {Domains} domains from PostgreSQL",
    prompts.Count, tools.Count, templates.Count, domains.Count);

// ── Startup: build semantic index ─────────────────────────────────────────────
var semanticSearch = app.Services.GetRequiredService<SemanticSearchService>();
// Build embedding index in background so startup is non-blocking
_ = Task.Run(async () =>
{
    try { await semanticSearch.BuildIndexAsync(prompts); }
    catch (Exception ex) { app.Logger.LogError(ex, "Failed to build semantic index on startup"); }
});

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapControllers();

// ── Startup connectivity checks ───────────────────────────────────────────────
_ = Task.Run(async () =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var settings = app.Services.GetRequiredService<IOptions<AvaSettings>>().Value;
    var authSettings = app.Services.GetRequiredService<IOptions<AuthSettings>>().Value;

    log.LogInformation("=== AVA-API Startup Diagnostics ===");
    log.LogInformation("Environment: {Env}", app.Environment.EnvironmentName);
    log.LogInformation("Auth mode: {Mode}", authSettings.UseCookieForwarding ? "cookie-forwarding" : "password-auth");

    // Session store / Redis status
    var sessionStore = app.Services.GetRequiredService<ISessionStore>();
    var storeType = sessionStore is RedisSessionStore ? "Redis" : "InMemory";
    log.LogInformation("Session store: {Type}", storeType);
    if (sessionStore is RedisSessionStore)
    {
        var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
        log.LogInformation("Redis: connected={Connected}, endpoint={Endpoint}",
            redis.IsConnected, redis.GetEndPoints().FirstOrDefault()?.ToString() ?? "unknown");
    }
    else
    {
        log.LogInformation("Redis: not configured (using InMemory fallback)");
    }

    // Ava.Vectors status
    var vectorIndex = app.Services.GetService<IVectorIndex<Ava.Api.Models.PromptDefinition>>();
    if (vectorIndex != null)
    {
        log.LogInformation("Ava.Vectors: loaded, index available");
    }

    // KB service check
    var kbUrl = settings.KnowledgeBase.KBServiceBaseUrl;
    log.LogInformation("KB Service BaseUrl: {Url}", kbUrl);
    try
    {
        using var http = new HttpClient();
        var kbHealthUrl = kbUrl.TrimEnd('/').Replace("/avakb/", "/") + "/health";
        log.LogInformation("KB health check: GET {Url}", kbHealthUrl);
        var resp = await http.GetAsync(kbHealthUrl);
        log.LogInformation("KB health response: {Status} {Body}", resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
    catch (Exception ex)
    {
        log.LogError(ex, "KB health check FAILED");
    }

    // External service checks
    foreach (var (name, svc) in settings.Services)
    {
        log.LogInformation("Service '{Name}': BaseUrl={Url}, Enabled={Enabled}, RequiresCdsAuth={Auth}",
            name, svc.BaseUrl, svc.Enabled, svc.RequiresCdsAuth);

        if (!svc.Enabled) continue;
        try
        {
            using var http = new HttpClient();
            var healthUrl = svc.BaseUrl.TrimEnd('/') + "/health";
            log.LogInformation("  Health check: GET {Url}", healthUrl);
            var resp = await http.GetAsync(healthUrl);
            var body = await resp.Content.ReadAsStringAsync();
            if (body.Length > 500) body = body[..500] + "...";
            log.LogInformation("  Response: {Status} {Body}", resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            log.LogWarning("  Health check FAILED for '{Name}': {Error}", name, ex.Message);
        }
    }

    log.LogInformation("=== End Startup Diagnostics ===");
});

app.Run();
