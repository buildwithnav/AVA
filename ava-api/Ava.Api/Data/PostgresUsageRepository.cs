using Dapper;
using Ava.Api.Models;

namespace Ava.Api.Data;

/// <summary>
/// Persists and queries usage_events in Postgres.
/// Written once per completed agent run (and per PromptController.Execute call).
/// Used for cost tracking, auditing, and the /api/usage endpoints.
/// </summary>
public class PostgresUsageRepository
{
    private readonly DbConnectionFactory _db;
    private readonly ILogger<PostgresUsageRepository> _logger;

    public PostgresUsageRepository(DbConnectionFactory db, ILogger<PostgresUsageRepository> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>Insert a single usage event row. Fire-and-forget safe (logs on error).</summary>
    public async Task InsertAsync(UsageEvent evt)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync("""
                INSERT INTO usage_events
                    (agent_run_id, conversation_id, session_id, user_id, username, domain,
                     model_id, input_tokens, output_tokens, total_tokens,
                     estimated_cost_usd, prompt_id, created_at)
                VALUES
                    (@AgentRunId, @ConversationId, @SessionId, @UserId, @Username, @Domain,
                     @ModelId, @InputTokens, @OutputTokens, @TotalTokens,
                     @EstimatedCostUsd, @PromptId, @CreatedAt)
                """,
                new
                {
                    evt.AgentRunId,
                    evt.ConversationId,
                    evt.SessionId,
                    evt.UserId,
                    evt.Username,
                    evt.Domain,
                    evt.ModelId,
                    evt.InputTokens,
                    evt.OutputTokens,
                    evt.TotalTokens,
                    evt.EstimatedCostUsd,
                    evt.PromptId,
                    evt.CreatedAt
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert usage event for run {RunId}", evt.AgentRunId);
        }
    }

    /// <summary>
    /// Returns aggregated usage summary. Optionally filtered by userId, date range.
    /// </summary>
    public async Task<UsageSummary> GetSummaryAsync(
        string? userId = null,
        DateTime? from = null,
        DateTime? to = null)
    {
        await using var conn = await _db.OpenConnectionAsync();

        var where = BuildWhereClause(userId, from, to);
        var rows = await conn.QueryAsync(
            $"""
            SELECT
                model_id,
                domain,
                COUNT(*)                    AS runs,
                COALESCE(SUM(input_tokens),  0) AS input_tokens,
                COALESCE(SUM(output_tokens), 0) AS output_tokens,
                COALESCE(SUM(total_tokens),  0) AS total_tokens,
                COALESCE(SUM(estimated_cost_usd), 0) AS cost_usd
            FROM usage_events
            {where.Sql}
            GROUP BY model_id, domain
            ORDER BY total_tokens DESC
            """,
            where.Parameters);

        var summary = new UsageSummary();

        foreach (var row in rows)
        {
            summary.TotalRuns         += (int)(long)row.runs;
            summary.TotalInputTokens  += (int)(long)row.input_tokens;
            summary.TotalOutputTokens += (int)(long)row.output_tokens;
            summary.TotalTokens       += (int)(long)row.total_tokens;
            summary.TotalCostUsd      += (decimal)row.cost_usd;

            var modelKey = (string?)row.model_id ?? "unknown";
            if (!summary.ByModel.TryGetValue(modelKey, out var mSummary))
            {
                mSummary = new ModelUsageSummary();
                summary.ByModel[modelKey] = mSummary;
            }
            mSummary.Runs         += (int)(long)row.runs;
            mSummary.InputTokens  += (int)(long)row.input_tokens;
            mSummary.OutputTokens += (int)(long)row.output_tokens;
            mSummary.TotalTokens  += (int)(long)row.total_tokens;
            mSummary.CostUsd      += (decimal)row.cost_usd;

            var domainKey = (string?)row.domain ?? "unknown";
            if (!summary.ByDomain.TryGetValue(domainKey, out var dSummary))
            {
                dSummary = new DomainUsageSummary();
                summary.ByDomain[domainKey] = dSummary;
            }
            dSummary.Runs        += (int)(long)row.runs;
            dSummary.TotalTokens += (int)(long)row.total_tokens;
            dSummary.CostUsd     += (decimal)row.cost_usd;
        }

        return summary;
    }

    /// <summary>Returns individual usage events for a specific user, newest first.</summary>
    public async Task<List<UsageEvent>> GetByUserAsync(
        string userId,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 100)
    {
        await using var conn = await _db.OpenConnectionAsync();

        var where = BuildWhereClause(userId, from, to);
        var limitParams = new DynamicParameters(where.Parameters);
        limitParams.Add("Limit", limit);
        var rows = await conn.QueryAsync(
            $"""
            SELECT
                agent_run_id, conversation_id, session_id, user_id, username, domain,
                model_id, input_tokens, output_tokens, total_tokens,
                estimated_cost_usd, prompt_id, created_at
            FROM usage_events
            {where.Sql}
            ORDER BY created_at DESC
            LIMIT @Limit
            """,
            limitParams);

        return rows.Select(r => new UsageEvent(
            AgentRunId:       r.agent_run_id,
            ConversationId:   r.conversation_id,
            SessionId:        r.session_id,
            UserId:           r.user_id,
            Username:         r.username,
            Domain:           r.domain,
            ModelId:          r.model_id,
            InputTokens:      (int)r.input_tokens,
            OutputTokens:     (int)r.output_tokens,
            TotalTokens:      (int)r.total_tokens,
            EstimatedCostUsd: (decimal)r.estimated_cost_usd,
            PromptId:         r.prompt_id,
            CreatedAt:        (DateTime)r.created_at
        )).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string Sql, object Parameters) BuildWhereClause(
        string? userId, DateTime? from, DateTime? to)
    {
        var conditions = new List<string>();
        var p = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(userId))
        {
            conditions.Add("user_id = @UserId");
            p.Add("UserId", userId);
        }
        if (from.HasValue)
        {
            conditions.Add("created_at >= @From");
            p.Add("From", from.Value.ToUniversalTime());
        }
        if (to.HasValue)
        {
            conditions.Add("created_at <= @To");
            p.Add("To", to.Value.ToUniversalTime());
        }

        var sql = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (sql, p);
    }
}
