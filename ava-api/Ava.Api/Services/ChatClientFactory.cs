using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Ava.Api.Config;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Resolves ChatClient in priority order:
///   1. Azure OpenAI (primary — used when no GitHub token is present in the session)
///   2. GitHub Copilot API (premium path — requires a GitHub OAuth token in the session)
///
/// An optional modelOverride can request a specific model:
///   - GitHub Copilot path: modelOverride is used directly as the GitHub model ID.
///   - Azure AOAI path: modelOverride is resolved through AzureOpenAi:ModelMappings;
///     falls back to ChatDeployment if not found in the map.
/// </summary>
public class ChatClientFactory : IChatClientFactory
{
    private readonly AvaSettings _settings;
    private readonly UserSessionService _userSession;
    private readonly ILogger<ChatClientFactory> _logger;

    public ChatClientFactory(
        IOptions<AvaSettings> settings,
        UserSessionService userSession,
        ILogger<ChatClientFactory> logger)
    {
        _settings    = settings.Value;
        _userSession = userSession;
        _logger      = logger;
    }

    public async Task<ChatClient> CreateAsync(string? sessionId = null, string? modelOverride = null)
    {
        var session = await _userSession.GetAsync(sessionId);
        var githubToken = session?.GitHubToken;

        if (!string.IsNullOrEmpty(githubToken))
        {
            var modelId = ResolveGitHubModel(session, modelOverride);
            _logger.LogInformation("ChatClientFactory: GitHub Copilot path, model='{Model}' session={Session}",
                modelId, sessionId?[..8] ?? "null");

            var options = new OpenAIClientOptions
            {
                Endpoint  = new Uri(_settings.GitHubCopilot.Endpoint),
                Transport = new HttpClientPipelineTransport(CreateNoProxyHttpClient())
            };
            options.AddPolicy(new CopilotBearerPolicy(githubToken), PipelinePosition.BeforeTransport);
            var ghClient = new OpenAIClient(new ApiKeyCredential("_"), options);
            return ghClient.GetChatClient(modelId);
        }

        // Primary path: Azure OpenAI
        var deployment = ResolveAzureDeployment(modelOverride);
        _logger.LogInformation("ChatClientFactory: Azure OpenAI path, deployment='{Deployment}' session={Session}",
            deployment, sessionId?[..8] ?? "null");
        var aoaiClient = new AzureOpenAIClient(
            new Uri(_settings.AzureOpenAi.Endpoint),
            new AzureKeyCredential(_settings.AzureOpenAi.ApiKey));
        return aoaiClient.GetChatClient(deployment);
    }

    /// <summary>
    /// Returns the resolved model ID string that would be used for a call with the given session
    /// and optional model override. Used by AgentService to record run.ModelId before the loop.
    /// </summary>
    public async Task<string> GetModelIdAsync(string? sessionId = null, string? modelOverride = null)
    {
        var session = await _userSession.GetAsync(sessionId);
        if (!string.IsNullOrEmpty(session?.GitHubToken))
            return ResolveGitHubModel(session, modelOverride);

        return ResolveAzureDeployment(modelOverride);
    }

    /// <summary>
    /// Creates Azure OpenAI client directly (used when GitHub Copilot returns 403).
    /// </summary>
    public ChatClient CreateAzureFallback()
    {
        _logger.LogWarning("ChatClientFactory: GitHub Copilot returned 403 — using Azure OpenAI deployment '{Deployment}'",
            _settings.AzureOpenAi.ChatDeployment);
        var aoaiClient = new AzureOpenAIClient(
            new Uri(_settings.AzureOpenAi.Endpoint),
            new AzureKeyCredential(_settings.AzureOpenAi.ApiKey));
        return aoaiClient.GetChatClient(_settings.AzureOpenAi.ChatDeployment);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string ResolveGitHubModel(UserSession? session, string? modelOverride)
    {
        if (!string.IsNullOrEmpty(modelOverride)) return modelOverride;
        if (!string.IsNullOrEmpty(session?.SelectedModel)) return session!.SelectedModel!;
        return _settings.GitHubCopilot.DefaultChatModel;
    }

    private string ResolveAzureDeployment(string? modelOverride)
    {
        if (!string.IsNullOrEmpty(modelOverride)
            && _settings.AzureOpenAi.ModelMappings.TryGetValue(modelOverride, out var mapped))
            return mapped;

        // If modelOverride is set but not in mappings, log and fall back to default deployment
        if (!string.IsNullOrEmpty(modelOverride))
        {
            _logger.LogWarning(
                "ChatClientFactory: modelOverride '{Override}' not found in AzureOpenAi:ModelMappings — using default deployment '{Default}'",
                modelOverride, _settings.AzureOpenAi.ChatDeployment);
        }

        return _settings.AzureOpenAi.ChatDeployment;
    }

    /// <summary>
    /// Creates an HttpClient that bypasses the system proxy (Zscaler PAC file).
    /// On machines without a proxy this is a harmless no-op.
    /// </summary>
    private static HttpClient CreateNoProxyHttpClient()
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy { BypassProxyOnLocal = true },
            UseProxy = false
        };
        return new HttpClient(handler);
    }
}
