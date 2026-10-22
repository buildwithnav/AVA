using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Auto-summarizes old conversation messages when history exceeds a threshold.
/// Uses a dedicated small model to keep compaction fast and cheap.
/// </summary>
public class CompactionService
{
    private readonly ConversationService _conversation;
    private readonly IChatClientFactory _chatFactory;
    private readonly AvaSettings _settings;
    private readonly ILogger<CompactionService> _logger;

    public CompactionService(
        ConversationService conversation,
        IChatClientFactory chatFactory,
        IOptions<AvaSettings> settings,
        ILogger<CompactionService> logger)
    {
        _conversation = conversation;
        _chatFactory  = chatFactory;
        _settings     = settings.Value;
        _logger       = logger;
    }

    /// <summary>
    /// Check if conversation needs compaction and perform it if so.
    /// Called before each LLM call in the tool loop.
    /// </summary>
    public async Task CompactIfNeededAsync(string conversationId, string? sessionId)
    {
        var history = await _conversation.GetHistoryAsync(conversationId);
        var threshold = _settings.Conversation.CompactionThreshold;
        var retainRecent = _settings.Conversation.RetainRecentMessages;

        if (history.Count < threshold) return;

        var toCompact = history.Take(history.Count - retainRecent).ToList();
        if (toCompact.Count == 0) return;

        _logger.LogInformation(
            "Compacting conversation {Id}: {Total} messages, compacting {Count}, retaining {Retain}",
            conversationId, history.Count, toCompact.Count, retainRecent);

        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "Summarize this conversation concisely (3-5 sentences). " +
                    "Include: topics discussed, datasets queried (names + key findings), " +
                    "user goals/preferences, any pending actions. " +
                    "Do NOT include greetings or filler.")
            };

            foreach (var msg in toCompact)
            {
                if (msg.Role == "user")
                    messages.Add(new UserChatMessage(msg.Content));
                else if (msg.Role == "assistant")
                    messages.Add(new AssistantChatMessage(msg.Content));
                else // system (previous summary)
                    messages.Add(new SystemChatMessage(msg.Content));
            }

            var client = await _chatFactory.CreateAsync(sessionId);
            var response = await client.CompleteChatAsync(messages);
            var summary = response.Value.Content[0].Text.Trim();

            await _conversation.ReplaceOldestWithSummaryAsync(conversationId, summary, retainRecent);
            _logger.LogInformation("Compaction complete for {Id}: summary length={Len}", conversationId, summary.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compaction failed for conversation {Id}", conversationId);
            // Non-fatal — conversation continues with full history
        }
    }
}
