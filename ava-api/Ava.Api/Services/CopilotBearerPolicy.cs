using System.ClientModel.Primitives;

namespace Ava.Api.Services;

/// <summary>
/// Pipeline policy that configures headers for api.githubcopilot.com.
/// Copilot API requires specific headers beyond just Authorization.
/// </summary>
internal sealed class CopilotBearerPolicy : PipelinePolicy
{
    private readonly string _bearerToken;

    public CopilotBearerPolicy(string bearerToken)
    {
        _bearerToken = bearerToken;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplyHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplyHeaders(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void ApplyHeaders(PipelineMessage message)
    {
        // Remove SDK-injected API key headers
        message.Request.Headers.Remove("api-key");
        message.Request.Headers.Remove("x-api-key");
        
        // Required: Bearer token authentication
        message.Request.Headers.Set("Authorization", $"Bearer {_bearerToken}");
        
        // Required: Copilot API headers (missing these causes 403)
        message.Request.Headers.Set("Editor-Version", "vscode/1.96.0");
        message.Request.Headers.Set("Editor-Plugin-Version", "copilot/1.250.0");
        message.Request.Headers.Set("Copilot-Integration-Id", "vscode-chat");
        message.Request.Headers.Set("openai-intent", "conversation-panel");
        message.Request.Headers.Set("User-Agent", "GitHubCopilotChat/1.250.0");
    }
}
