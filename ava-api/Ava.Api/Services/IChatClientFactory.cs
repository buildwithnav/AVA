using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Resolves the correct ChatClient based on the user's session.
/// Primary path: Azure OpenAI (no GitHub token required).
/// Premium path: GitHub Copilot API (requires GitHub OAuth token in session).
/// An optional modelOverride selects a specific model/deployment for both paths.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Creates a ChatClient for the given session.
    /// Optional modelOverride selects a specific model (GitHub path) or
    /// deployment via ModelMappings (Azure AOAI path).
    /// </summary>
    Task<ChatClient> CreateAsync(string? sessionId = null, string? modelOverride = null);

    /// <summary>
    /// Returns the resolved model ID string without creating a client.
    /// Used to record run.ModelId before the first LLM call.
    /// </summary>
    Task<string> GetModelIdAsync(string? sessionId = null, string? modelOverride = null);

    /// <summary>
    /// Creates Azure OpenAI client directly. Used when GitHub Copilot returns 403.
    /// </summary>
    ChatClient CreateAzureFallback();
}
