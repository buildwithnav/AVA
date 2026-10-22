using System.Text.Json;
using System.Text.Json.Serialization;
using Ava.Api.Config;
using Ava.Api.Models;
using StackExchange.Redis;

namespace Ava.Api.Services;

/// <summary>
/// Redis-backed implementation of ISessionStore.
/// Key patterns:
///   session:{id}          → Hash (field-level access for tokens/model)
///   run:{id}              → String (JSON-serialized AgentRun)
///   runs_by_conv:{id}     → SortedSet (run IDs scored by StartedAt ticks)
///   conv:{id}             → List (JSON messages appended via RPUSH)
///   snap:{id}             → List used as stack (LPUSH/LPOP)
///   export:{queryId}      → String (JSON-serialized KbExportEntry)
/// </summary>
public class RedisSessionStore : ISessionStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisSessionStore> _logger;

    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan RunTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ConversationTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public RedisSessionStore(IConnectionMultiplexer redis, ILogger<RedisSessionStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private IDatabase Db => _redis.GetDatabase();

    // ── User Sessions ─────────────────────────────────────────────────────────

    public async Task<UserSession?> GetSessionAsync(string sessionId)
    {
        var key = $"session:{sessionId}";
        var entries = await Db.HashGetAllAsync(key);
        if (entries.Length == 0) return null;

        await Db.KeyExpireAsync(key, SessionTtl);

        var session = new UserSession();
        foreach (var entry in entries)
        {
            switch (entry.Name.ToString())
            {
                case "github_token": session.GitHubToken = entry.Value; break;
                case "ado_pat": session.AdoPat = entry.Value; break;
                case "selected_model": session.SelectedModel = entry.Value; break;
            }
        }
        return session;
    }

    public async Task SetSessionFieldAsync(string sessionId, string field, string? value)
    {
        var key = $"session:{sessionId}";
        if (value == null)
            await Db.HashDeleteAsync(key, field);
        else
            await Db.HashSetAsync(key, field, value);

        await Db.KeyExpireAsync(key, SessionTtl);
    }

    public async Task DeleteSessionFieldAsync(string sessionId, string field)
    {
        var key = $"session:{sessionId}";
        await Db.HashDeleteAsync(key, field);
        await Db.KeyExpireAsync(key, SessionTtl);
    }

    // ── Session ↔ Conversation Link ────────────────────────────────────────────

    public async Task LinkConversationToSessionAsync(string sessionId, string conversationId)
    {
        var key = $"convs_by_session:{sessionId}";
        await Db.SortedSetAddAsync(key, conversationId, DateTimeOffset.UtcNow.Ticks);
        await Db.KeyExpireAsync(key, SessionTtl);
    }

    public async Task<IReadOnlyList<string>> GetConversationsBySessionAsync(string sessionId)
    {
        var key = $"convs_by_session:{sessionId}";
        var members = await Db.SortedSetRangeByScoreAsync(key, order: Order.Descending);
        await Db.KeyExpireAsync(key, SessionTtl);
        return members.Select(m => m.ToString()).ToList();
    }

    // ── Agent Runs ────────────────────────────────────────────────────────────

    public async Task SaveRunAsync(AgentRun run)
    {
        var runKey = $"run:{run.AgentRunId}";
        var json = JsonSerializer.Serialize(run, JsonOpts);
        await Db.StringSetAsync(runKey, json, RunTtl);

        // Maintain sorted set for conversation → run lookups
        var convKey = $"runs_by_conv:{run.ConversationId}";
        await Db.SortedSetAddAsync(convKey, run.AgentRunId, run.StartedAt.Ticks);
        await Db.KeyExpireAsync(convKey, RunTtl);
    }

    public async Task<AgentRun?> GetRunAsync(string runId)
    {
        var key = $"run:{runId}";
        var json = await Db.StringGetAsync(key);
        if (json.IsNullOrEmpty) return null;

        await Db.KeyExpireAsync(key, RunTtl);
        return JsonSerializer.Deserialize<AgentRun>(json!, JsonOpts);
    }

    public async Task<IReadOnlyList<AgentRun>> GetRunsByConversationAsync(string conversationId)
    {
        var convKey = $"runs_by_conv:{conversationId}";
        var runIds = await Db.SortedSetRangeByScoreAsync(convKey, order: Order.Descending);

        var runs = new List<AgentRun>();
        foreach (var id in runIds)
        {
            var run = await GetRunAsync(id!);
            if (run != null) runs.Add(run);
        }
        return runs;
    }

    // ── Conversation History ──────────────────────────────────────────────────

    public async Task AddMessageAsync(string conversationId, ConversationMessage message)
    {
        var key = $"conv:{conversationId}";
        var json = JsonSerializer.Serialize(message, JsonOpts);
        await Db.ListRightPushAsync(key, json);

        // Trim to max 20 messages
        await Db.ListTrimAsync(key, -20, -1);
        await Db.KeyExpireAsync(key, ConversationTtl);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetHistoryAsync(string conversationId)
    {
        var key = $"conv:{conversationId}";
        var items = await Db.ListRangeAsync(key);

        await Db.KeyExpireAsync(key, ConversationTtl);

        var messages = new List<ConversationMessage>(items.Length);
        foreach (var item in items)
        {
            var msg = JsonSerializer.Deserialize<ConversationMessage>(item!, JsonOpts);
            if (msg != null) messages.Add(msg);
        }
        return messages;
    }

    public async Task ClearConversationAsync(string conversationId)
    {
        var convKey = $"conv:{conversationId}";
        var snapKey = $"snap:{conversationId}";
        await Db.KeyDeleteAsync(new RedisKey[] { convKey, snapKey });
    }

    public async Task ReplaceHistoryAsync(string conversationId, IReadOnlyList<ConversationMessage> messages)
    {
        var key = $"conv:{conversationId}";
        var tran = Db.CreateTransaction();

        _ = tran.KeyDeleteAsync(key);
        foreach (var msg in messages)
        {
            var json = JsonSerializer.Serialize(msg, JsonOpts);
            _ = tran.ListRightPushAsync(key, json);
        }
        _ = tran.KeyExpireAsync(key, ConversationTtl);

        await tran.ExecuteAsync();
    }

    // ── Conversation Snapshots ────────────────────────────────────────────────

    public async Task PushSnapshotAsync(string conversationId, ConversationSnapshot snapshot, int maxSnapshots)
    {
        var key = $"snap:{conversationId}";
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        await Db.ListLeftPushAsync(key, json);

        // Trim to max snapshots
        await Db.ListTrimAsync(key, 0, maxSnapshots - 1);
        await Db.KeyExpireAsync(key, ConversationTtl);
    }

    public async Task<ConversationSnapshot?> PopSnapshotAsync(string conversationId)
    {
        var key = $"snap:{conversationId}";
        var json = await Db.ListLeftPopAsync(key);
        if (json.IsNullOrEmpty) return null;

        return JsonSerializer.Deserialize<ConversationSnapshot>(json!, JsonOpts);
    }

    // ── KB CSV Exports ────────────────────────────────────────────────────────

    public async Task SaveExportAsync(string queryId, KbExportEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOpts);
        await Db.StringSetAsync($"export:{queryId}", json, KbExportCacheService.Ttl);
    }

    public async Task<KbExportEntry?> GetExportAsync(string queryId)
    {
        // No sliding expiry: the TTL bounds how stale a download can be relative to the
        // moment the query ran, so refreshing it on read would defeat the purpose.
        var json = await Db.StringGetAsync($"export:{queryId}");
        if (json.IsNullOrEmpty) return null;

        return JsonSerializer.Deserialize<KbExportEntry>(json!, JsonOpts);
    }
}
