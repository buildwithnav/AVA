using System.Collections.Concurrent;
using Ava.Api.Config;
using Ava.Api.Models;

namespace Ava.Api.Services;

/// <summary>
/// In-memory fallback implementation of ISessionStore.
/// Used when no Redis connection string is configured (local dev without Docker).
/// Mirrors original ConcurrentDictionary behavior with no persistence.
/// </summary>
public class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentRun> _runs = new();
    private readonly ConcurrentDictionary<string, List<ConversationMessage>> _conversations = new();
    private readonly ConcurrentDictionary<string, List<ConversationSnapshot>> _snapshots = new();
    private readonly ConcurrentDictionary<string, SortedSet<(long ticks, string convId)>> _sessionConvs = new();
    private readonly ConcurrentDictionary<string, KbExportEntry> _exports = new(StringComparer.Ordinal);

    // ── User Sessions ─────────────────────────────────────────────────────────

    public Task<UserSession?> GetSessionAsync(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task SetSessionFieldAsync(string sessionId, string field, string? value)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new UserSession());
        switch (field)
        {
            case "github_token": session.GitHubToken = value; break;
            case "ado_pat": session.AdoPat = value; break;
            case "selected_model": session.SelectedModel = value; break;
        }
        return Task.CompletedTask;
    }

    public Task DeleteSessionFieldAsync(string sessionId, string field)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            switch (field)
            {
                case "github_token": session.GitHubToken = null; break;
                case "ado_pat": session.AdoPat = null; break;
                case "selected_model": session.SelectedModel = null; break;
            }
        }
        return Task.CompletedTask;
    }

    // ── Session ↔ Conversation Link ────────────────────────────────────────────

    public Task LinkConversationToSessionAsync(string sessionId, string conversationId)
    {
        var set = _sessionConvs.GetOrAdd(sessionId, _ => new SortedSet<(long, string)>());
        lock (set)
        {
            set.Add((DateTimeOffset.UtcNow.Ticks, conversationId));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetConversationsBySessionAsync(string sessionId)
    {
        if (!_sessionConvs.TryGetValue(sessionId, out var set))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        lock (set)
        {
            var result = set.Reverse().Select(x => x.convId).Distinct().ToList();
            return Task.FromResult<IReadOnlyList<string>>(result);
        }
    }

    // ── Agent Runs ────────────────────────────────────────────────────────────

    public Task SaveRunAsync(AgentRun run)
    {
        _runs[run.AgentRunId] = run;
        return Task.CompletedTask;
    }

    public Task<AgentRun?> GetRunAsync(string runId)
    {
        _runs.TryGetValue(runId, out var run);
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<AgentRun>> GetRunsByConversationAsync(string conversationId)
    {
        var runs = _runs.Values
            .Where(r => r.ConversationId == conversationId)
            .OrderByDescending(r => r.StartedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentRun>>(runs);
    }

    // ── Conversation History ──────────────────────────────────────────────────

    public Task AddMessageAsync(string conversationId, ConversationMessage message)
    {
        var history = _conversations.GetOrAdd(conversationId, _ => new List<ConversationMessage>());
        lock (history)
        {
            history.Add(message);
            if (history.Count > 20)
                history.RemoveRange(0, history.Count - 20);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationMessage>> GetHistoryAsync(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var history))
        {
            lock (history)
                return Task.FromResult<IReadOnlyList<ConversationMessage>>(history.ToList());
        }
        return Task.FromResult<IReadOnlyList<ConversationMessage>>(Array.Empty<ConversationMessage>());
    }

    public Task ClearConversationAsync(string conversationId)
    {
        _conversations.TryRemove(conversationId, out _);
        _snapshots.TryRemove(conversationId, out _);
        return Task.CompletedTask;
    }

    public Task ReplaceHistoryAsync(string conversationId, IReadOnlyList<ConversationMessage> messages)
    {
        var history = _conversations.GetOrAdd(conversationId, _ => new List<ConversationMessage>());
        lock (history)
        {
            history.Clear();
            history.AddRange(messages);
        }
        return Task.CompletedTask;
    }

    // ── Conversation Snapshots ────────────────────────────────────────────────

    public Task PushSnapshotAsync(string conversationId, ConversationSnapshot snapshot, int maxSnapshots)
    {
        var stack = _snapshots.GetOrAdd(conversationId, _ => new List<ConversationSnapshot>());
        lock (stack)
        {
            stack.Insert(0, snapshot);
            if (stack.Count > maxSnapshots)
                stack.RemoveAt(stack.Count - 1);
        }
        return Task.CompletedTask;
    }

    public Task<ConversationSnapshot?> PopSnapshotAsync(string conversationId)
    {
        if (!_snapshots.TryGetValue(conversationId, out var stack))
            return Task.FromResult<ConversationSnapshot?>(null);

        lock (stack)
        {
            if (stack.Count == 0) return Task.FromResult<ConversationSnapshot?>(null);
            var snap = stack[0];
            stack.RemoveAt(0);
            return Task.FromResult<ConversationSnapshot?>(snap);
        }
    }

    // ── KB CSV Exports ────────────────────────────────────────────────────────

    public Task SaveExportAsync(string queryId, KbExportEntry entry)
    {
        PruneExpiredExports();
        _exports[queryId] = entry;
        return Task.CompletedTask;
    }

    public Task<KbExportEntry?> GetExportAsync(string queryId)
    {
        PruneExpiredExports();
        _exports.TryGetValue(queryId, out var entry);
        return Task.FromResult(entry);
    }

    /// <summary>Redis expires export keys itself; here we have to sweep them manually.</summary>
    private void PruneExpiredExports()
    {
        var cutoff = DateTime.UtcNow - KbExportCacheService.Ttl;
        foreach (var kv in _exports)
            if (kv.Value.CreatedAt < cutoff)
                _exports.TryRemove(kv.Key, out _);
    }
}
