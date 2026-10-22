using System.Text.RegularExpressions;
using Ava.Api.Models;

namespace Ava.Api.Services;

/// <summary>
/// A reproducible export: the exact datasets + SQL that produced a kb_query result.
/// The download endpoint re-runs this server-trusted SQL to stream the full CSV.
///
/// <see cref="DatasetVersions"/> maps each datasetId to a version marker captured at query
/// time (see <c>KbExportCacheService.VersionMarker</c>). The download endpoint recomputes the
/// markers and refuses the export if any dataset changed, so a stale/refreshed KB can never
/// silently return data that differs from what the user originally saw.
/// </summary>
public record KbExportEntry(
    string[] DatasetIds,
    string Sql,
    IReadOnlyDictionary<string, string> DatasetVersions,
    DateTime CreatedAt);

/// <summary>
/// TTL-bounded store mapping a queryId to the SQL that reproduces its results.
/// Populated when a kb_query succeeds; read by the CSV download endpoint.
///
/// Backed by <see cref="ISessionStore"/>, so with Redis configured download links survive
/// an API restart and work across multiple API instances. Without Redis the store falls
/// back to <see cref="InMemorySessionStore"/> and links are per-process, as before.
/// </summary>
public class KbExportCacheService
{
    /// <summary>How long a download link stays valid. Also used by the stores as the key TTL.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly ISessionStore _store;

    public KbExportCacheService(ISessionStore store) => _store = store;

    /// <summary>
    /// Builds a version marker for a dataset from the fields that change when its data is
    /// replaced or refreshed. Used to detect drift between query time and download time.
    /// </summary>
    public static string VersionMarker(KnowledgeBase kb)
        => $"{kb.LastRefreshedAt?.ToString("o") ?? "-"}|{kb.RowCount?.ToString() ?? "-"}|{kb.DataPath ?? "-"}";

    // Matches a trailing LIMIT/OFFSET clause at the very end of a query (in either order),
    // after any trailing semicolon/whitespace has been trimmed. Only the outermost trailing
    // clause is targeted — a LIMIT inside a subquery ends with ')' and is left untouched.
    private static readonly Regex TrailingLimitOffset = new(
        @"\s+(LIMIT\s+(ALL|\d+)(\s+OFFSET\s+\d+)?|OFFSET\s+\d+(\s+LIMIT\s+(ALL|\d+))?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Removes a trailing LIMIT/OFFSET clause so a CSV export returns the full result set.
    /// The model often adds "LIMIT n" defensively (the kb_query tool tells it results are
    /// capped), which would otherwise leak into the download. LIMITs inside subqueries are
    /// preserved because they don't sit at the end of the statement.
    /// </summary>
    public static string StripTrailingLimitOffset(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        var trimmed = sql.TrimEnd().TrimEnd(';').TrimEnd();
        return TrailingLimitOffset.Replace(trimmed, string.Empty);
    }

    /// <summary>Stores a reproducible query and returns its opaque queryId.</summary>
    public async Task<string> StoreAsync(string[] datasetIds, string sql, IReadOnlyDictionary<string, string> datasetVersions)
    {
        var queryId = Guid.NewGuid().ToString("N");
        await _store.SaveExportAsync(queryId, new KbExportEntry(datasetIds, sql, datasetVersions, DateTime.UtcNow));
        return queryId;
    }

    /// <summary>Returns the stored query, or null if it's missing or expired.</summary>
    public Task<KbExportEntry?> GetAsync(string queryId) => _store.GetExportAsync(queryId);
}
