using Ava.Api.Config;
using Ava.Api.Models;

namespace Ava.Api.Services;

/// <summary>
/// Abstraction over session/run/conversation persistence.
/// Implementations: RedisSessionStore (production) and InMemorySessionStore (local dev fallback).
/// </summary>
public interface ISessionStore
{
    // ── User Sessions ─────────────────────────────────────────────────────────

    Task<UserSession?> GetSessionAsync(string sessionId);
    Task SetSessionFieldAsync(string sessionId, string field, string? value);
    Task DeleteSessionFieldAsync(string sessionId, string field);

    // ── Session ↔ Conversation Link ────────────────────────────────────────────

    Task LinkConversationToSessionAsync(string sessionId, string conversationId);
    Task<IReadOnlyList<string>> GetConversationsBySessionAsync(string sessionId);

    // ── Agent Runs ────────────────────────────────────────────────────────────

    Task SaveRunAsync(AgentRun run);
    Task<AgentRun?> GetRunAsync(string runId);
    Task<IReadOnlyList<AgentRun>> GetRunsByConversationAsync(string conversationId);

    // ── Conversation History ──────────────────────────────────────────────────

    Task AddMessageAsync(string conversationId, ConversationMessage message);
    Task<IReadOnlyList<ConversationMessage>> GetHistoryAsync(string conversationId);
    Task ClearConversationAsync(string conversationId);
    Task ReplaceHistoryAsync(string conversationId, IReadOnlyList<ConversationMessage> messages);

    // ── Conversation Snapshots ────────────────────────────────────────────────

    Task PushSnapshotAsync(string conversationId, ConversationSnapshot snapshot, int maxSnapshots);
    Task<ConversationSnapshot?> PopSnapshotAsync(string conversationId);

    // ── KB CSV Exports ────────────────────────────────────────────────────────

    Task SaveExportAsync(string queryId, KbExportEntry entry);
    Task<KbExportEntry?> GetExportAsync(string queryId);
}
