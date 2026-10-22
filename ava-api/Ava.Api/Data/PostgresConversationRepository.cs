using Dapper;
using Ava.Api.Models;

namespace Ava.Api.Data;

/// <summary>
/// Persists conversations and messages to Postgres for traceability and future querying.
/// Redis remains the live session cache; Postgres is the durable audit archive.
/// Written once per completed agent run turn (upsert conversation + append two messages).
/// </summary>
public class PostgresConversationRepository
{
    private readonly DbConnectionFactory _db;
    private readonly ILogger<PostgresConversationRepository> _logger;

    public PostgresConversationRepository(DbConnectionFactory db, ILogger<PostgresConversationRepository> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// Upserts the conversation header row.
    /// Creates it on first message, updates updated_at on every subsequent turn.
    /// </summary>
    public async Task UpsertConversationAsync(ConversationRecord conv)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync("""
                INSERT INTO conversations (id, session_id, user_id, username, domain, created_at, updated_at)
                VALUES (@Id, @SessionId, @UserId, @Username, @Domain, @CreatedAt, @UpdatedAt)
                ON CONFLICT (id) DO UPDATE
                    SET updated_at = EXCLUDED.updated_at,
                        session_id = COALESCE(conversations.session_id, EXCLUDED.session_id),
                        username   = CASE WHEN EXCLUDED.username <> '' THEN EXCLUDED.username ELSE conversations.username END
                """,
                new
                {
                    conv.Id,
                    conv.SessionId,
                    conv.UserId,
                    conv.Username,
                    conv.Domain,
                    conv.CreatedAt,
                    UpdatedAt = DateTime.UtcNow
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert conversation {ConversationId}", conv.Id);
        }
    }

    /// <summary>
    /// Appends one user message row and one assistant message row for a completed turn.
    /// </summary>
    public async Task AppendMessagesAsync(
        string conversationId,
        string? agentRunId,
        string userContent,
        string assistantContent)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            var now = DateTime.UtcNow;

            await conn.ExecuteAsync("""
                INSERT INTO conversation_messages
                    (conversation_id, agent_run_id, role, content, created_at)
                VALUES
                    (@ConversationId, @AgentRunId, 'user',      @UserContent,      @Now),
                    (@ConversationId, @AgentRunId, 'assistant', @AssistantContent, @Now)
                """,
                new
                {
                    ConversationId   = conversationId,
                    AgentRunId       = agentRunId,
                    UserContent      = userContent,
                    AssistantContent = assistantContent,
                    Now              = now
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append messages for run {RunId}", agentRunId);
        }
    }

    /// <summary>Returns conversations for a user, newest first.</summary>
    public async Task<List<ConversationRecord>> GetByUserAsync(string userId, int limit = 50)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync("""
            SELECT id, session_id, user_id, username, domain, created_at, updated_at
            FROM conversations
            WHERE user_id = @UserId
            ORDER BY updated_at DESC
            LIMIT @Limit
            """,
            new { UserId = userId, Limit = limit });

        return rows.Select(r => new ConversationRecord(
            Id:        r.id,
            SessionId: r.session_id,
            UserId:    r.user_id,
            Username:  r.username,
            Domain:    r.domain,
            CreatedAt: (DateTime)r.created_at,
            UpdatedAt: (DateTime)r.updated_at
        )).ToList();
    }

    /// <summary>Returns all archived messages for a conversation, oldest first.</summary>
    public async Task<List<ConversationMessageRecord>> GetMessagesAsync(string conversationId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync("""
            SELECT id, conversation_id, agent_run_id, role, content, created_at
            FROM conversation_messages
            WHERE conversation_id = @ConversationId
            ORDER BY created_at ASC, id ASC
            """,
            new { ConversationId = conversationId });

        return rows.Select(r => new ConversationMessageRecord(
            Id:             (long)r.id,
            ConversationId: r.conversation_id,
            AgentRunId:     r.agent_run_id,
            Role:           r.role,
            Content:        r.content,
            CreatedAt:      (DateTime)r.created_at
        )).ToList();
    }
}
