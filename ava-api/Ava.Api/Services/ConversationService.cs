using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services;

/// <summary>
/// Manages conversation history with snapshot support for undo
/// and compaction support for long conversations.
/// Delegates persistence to ISessionStore (Redis or InMemory).
/// </summary>
public class ConversationService
{
    private readonly ISessionStore _store;
    private readonly int _maxSnapshots;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(ISessionStore store, IOptions<AvaSettings> settings, ILogger<ConversationService> logger)
    {
        _store = store;
        _maxSnapshots = settings.Value.Conversation.MaxSnapshots;
        _logger = logger;
    }

    public async Task AddMessageAsync(string conversationId, string role, string content)
    {
        var message = new ConversationMessage { Role = role, Content = content };
        await _store.AddMessageAsync(conversationId, message);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetHistoryAsync(string conversationId)
    {
        return await _store.GetHistoryAsync(conversationId);
    }

    public async Task ClearAsync(string conversationId)
    {
        await _store.ClearConversationAsync(conversationId);
        _logger.LogInformation("Cleared conversation {Id}", conversationId);
    }

    // ── Snapshot support for undo ─────────────────────────────────────────────

    public async Task CreateSnapshotAsync(string conversationId)
    {
        var history = await _store.GetHistoryAsync(conversationId);
        var snapshot = new ConversationSnapshot
        {
            Messages = history.ToList(),
            Timestamp = DateTime.UtcNow
        };
        await _store.PushSnapshotAsync(conversationId, snapshot, _maxSnapshots);
    }

    public async Task<ConversationSnapshot?> PopSnapshotAsync(string conversationId)
    {
        return await _store.PopSnapshotAsync(conversationId);
    }

    public async Task RestoreSnapshotAsync(string conversationId, ConversationSnapshot snapshot)
    {
        await _store.ReplaceHistoryAsync(conversationId, snapshot.Messages);
    }

    // ── Compaction support ────────────────────────────────────────────────────

    /// <summary>
    /// Replace the oldest N messages with a single summary message.
    /// Used by CompactionService after summarizing old context.
    /// </summary>
    public async Task ReplaceOldestWithSummaryAsync(string conversationId, string summary, int retainRecent)
    {
        var history = await _store.GetHistoryAsync(conversationId);
        if (history.Count <= retainRecent) return;

        var recentMessages = history.TakeLast(retainRecent).ToList();
        var newHistory = new List<ConversationMessage>
        {
            new ConversationMessage
            {
                Role = "system",
                Content = $"[Conversation summary]: {summary}",
                Timestamp = DateTime.UtcNow
            }
        };
        newHistory.AddRange(recentMessages);
        await _store.ReplaceHistoryAsync(conversationId, newHistory);
    }
}
