using System.ClientModel;
using System.Text;
using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Shared utility for streaming LLM completions. Used by AgentService (conversational
/// reply + tool loop) and all AI stage handlers (extract, generate, analyze).
/// Replaces every CompleteChatAsync call in the codebase.
/// Automatically falls back to Azure OpenAI if GitHub Copilot returns 403.
/// Applies the configured DefaultTemperature to all calls that don't explicitly set one.
/// </summary>
public class StreamingChatHelper
{
    private readonly IChatClientFactory _chatFactory;
    private readonly AvaSettings _settings;
    private readonly ILogger<StreamingChatHelper> _logger;

    public StreamingChatHelper(
        IChatClientFactory chatFactory,
        IOptions<AvaSettings> settings,
        ILogger<StreamingChatHelper> logger)
    {
        _chatFactory = chatFactory;
        _settings    = settings.Value;
        _logger      = logger;
    }

    /// <summary>
    /// Streams a plain text completion (no tools). Yields token events as they arrive.
    /// Returns the full accumulated text and token usage for saving to conversation history.
    /// Automatically falls back to Azure OpenAI on 403.
    /// </summary>
    public async Task<(string FullText, ChatTokenUsage? Usage)> StreamTextAsync(
        ChatClient client,
        IList<ChatMessage> messages,
        Func<StreamEvent, ValueTask> emit,
        ChatCompletionOptions? options = null)
    {
        try
        {
            return await StreamTextCoreAsync(client, messages, emit, options);
        }
        catch (ClientResultException ex) when (ex.Status == 403)
        {
            _logger.LogWarning("GitHub Copilot returned 403, falling back to Azure OpenAI");
            var fallbackClient = _chatFactory.CreateAzureFallback();
            return await StreamTextCoreAsync(fallbackClient, messages, emit, options);
        }
    }

    private async Task<(string FullText, ChatTokenUsage? Usage)> StreamTextCoreAsync(
        ChatClient client,
        IList<ChatMessage> messages,
        Func<StreamEvent, ValueTask> emit,
        ChatCompletionOptions? options = null)
    {
        options = ApplyDefaultTemperature(options);
        var text = new StringBuilder();
        ChatTokenUsage? usage = null;

        await foreach (var update in client.CompleteChatStreamingAsync(messages, options))
        {
            foreach (var part in update.ContentUpdate)
            {
                text.Append(part.Text);
                await emit(new StreamEvent { Type = "token", Data = part.Text });
            }
            if (update.Usage != null) usage = update.Usage;
        }

        return (text.ToString(), usage);
    }

    /// <summary>
    /// Streams a completion that may include tool calls. Streams text tokens in real-time.
    /// Accumulates tool call arguments across chunks (OpenAI streams them in fragments).
    /// Returns either text or tool calls — never both in practice.
    /// Automatically falls back to Azure OpenAI on 403.
    /// </summary>
    public async Task<StreamingResult> StreamWithToolsAsync(
        ChatClient client,
        IList<ChatMessage> messages,
        ChatCompletionOptions options,
        Func<StreamEvent, ValueTask> emit)
    {
        try
        {
            return await StreamWithToolsCoreAsync(client, messages, options, emit);
        }
        catch (ClientResultException ex) when (ex.Status == 403)
        {
            _logger.LogWarning("GitHub Copilot returned 403, falling back to Azure OpenAI");
            var fallbackClient = _chatFactory.CreateAzureFallback();
            return await StreamWithToolsCoreAsync(fallbackClient, messages, options, emit);
        }
    }

    private async Task<StreamingResult> StreamWithToolsCoreAsync(
        ChatClient client,
        IList<ChatMessage> messages,
        ChatCompletionOptions options,
        Func<StreamEvent, ValueTask> emit)
    {
        options = ApplyDefaultTemperature(options) ?? new ChatCompletionOptions();
        var text = new StringBuilder();
        var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
        ChatFinishReason? finishReason = null;
        ChatTokenUsage? usage = null;

        await foreach (var update in client.CompleteChatStreamingAsync(messages, options))
        {
            // Stream text tokens immediately
            foreach (var part in update.ContentUpdate)
            {
                text.Append(part.Text);
                await emit(new StreamEvent { Type = "token", Data = part.Text });
            }

            // Accumulate tool call fragments (id, name, argument chunks)
            foreach (var toolUpdate in update.ToolCallUpdates)
            {
                if (!toolCallBuilders.TryGetValue(toolUpdate.Index, out var builder))
                {
                    builder = new ToolCallBuilder();
                    toolCallBuilders[toolUpdate.Index] = builder;
                }
                if (toolUpdate.ToolCallId != null) builder.Id = toolUpdate.ToolCallId;
                if (toolUpdate.FunctionName != null) builder.Name = toolUpdate.FunctionName;
                if (toolUpdate.FunctionArgumentsUpdate is { } argUpdate)
                {
                    try { builder.Arguments.Append(argUpdate.ToString()); }
                    catch (ArgumentNullException) { /* BinaryData with null bytes — skip */ }
                }
            }

            if (update.FinishReason.HasValue) finishReason = update.FinishReason;
            if (update.Usage != null) usage = update.Usage;
        }

        return new StreamingResult
        {
            Text = text.Length > 0 ? text.ToString() : null,
            ToolCalls = toolCallBuilders.Count > 0
                ? toolCallBuilders.Values.Select(b => b.Build()).ToList()
                : null,
            FinishReason = finishReason ?? ChatFinishReason.Stop,
            Usage = usage
        };
    }

    /// <summary>
    /// Applies DefaultTemperature from config to the options object when:
    ///   - The configured value is not 1.0 (model default — no-op to set explicitly), AND
    ///   - The options don't already have a Temperature set.
    /// Returns a (possibly new) options object. Safe to call with null options.
    /// </summary>
    private ChatCompletionOptions? ApplyDefaultTemperature(ChatCompletionOptions? options)
    {
        var defaultTemp = _settings.GitHubCopilot.DefaultTemperature;

        // 1.0 is the model default — no need to set it explicitly
        if (Math.Abs(defaultTemp - 1.0f) < 0.001f) return options;

        // Don't override an explicitly set temperature
        if (options?.Temperature != null) return options;

        var result = options ?? new ChatCompletionOptions();
        result.Temperature = defaultTemp;
        return result;
    }
}

public class StreamingResult
{
    public string? Text { get; set; }
    public IList<ChatToolCall>? ToolCalls { get; set; }
    public ChatFinishReason FinishReason { get; set; }
    public ChatTokenUsage? Usage { get; set; }
    public bool IsToolCall => ToolCalls != null && ToolCalls.Count > 0;
}

public class ToolCallBuilder
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StringBuilder Arguments { get; } = new();
    public ChatToolCall Build() =>
        ChatToolCall.CreateFunctionToolCall(Id, Name, BinaryData.FromString(Arguments.ToString()));
}
