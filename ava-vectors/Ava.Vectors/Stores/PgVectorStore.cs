using Npgsql;
using Pgvector;

namespace Ava.Vectors.Stores;

/// <summary>
/// Vector store backed by PostgreSQL with the pgvector extension.
/// Uses a single table with a collection column for multi-tenant isolation.
/// </summary>
public class PgVectorStore : IVectorStore
{
    private readonly Func<Task<NpgsqlConnection>> _connectionFactory;

    /// <summary>
    /// Creates a PgVectorStore with an async connection factory.
    /// The factory should return an opened NpgsqlConnection configured with pgvector support.
    /// </summary>
    public PgVectorStore(Func<Task<NpgsqlConnection>> connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Dictionary<string, float[]>> LoadCacheAsync(string collection)
    {
        await using var conn = await _connectionFactory();

        await EnsureTableAsync(conn);

        await using var cmd = new NpgsqlCommand(
            "SELECT item_id, content_hash, embedding FROM vector_embeddings WHERE collection = @Collection", conn);
        cmd.Parameters.AddWithValue("Collection", collection);

        await using var reader = await cmd.ExecuteReaderAsync();
        var cache = new Dictionary<string, float[]>();

        while (await reader.ReadAsync())
        {
            var itemId = reader.GetString(0);
            var contentHash = reader.GetString(1);
            var vector = reader.GetFieldValue<Vector>(2);
            cache[$"{itemId}:{contentHash}"] = vector.ToArray();
        }

        return cache;
    }

    public async Task SaveEmbeddingsAsync(string collection, List<VectorEntry> entries)
    {
        await using var conn = await _connectionFactory();

        await EnsureTableAsync(conn);

        foreach (var entry in entries)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO vector_embeddings (collection, item_id, content_hash, embedding)
                VALUES (@Collection, @ItemId, @ContentHash, @Embedding)
                ON CONFLICT (collection, item_id, content_hash)
                DO UPDATE SET embedding = EXCLUDED.embedding
                """, conn);
            cmd.Parameters.AddWithValue("Collection", collection);
            cmd.Parameters.AddWithValue("ItemId", entry.Id);
            cmd.Parameters.AddWithValue("ContentHash", entry.ContentHash);
            cmd.Parameters.AddWithValue("Embedding", new Vector(entry.Vector));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task TruncateAsync(string collection)
    {
        await using var conn = await _connectionFactory();

        await EnsureTableAsync(conn);

        await using var cmd = new NpgsqlCommand(
            "DELETE FROM vector_embeddings WHERE collection = @Collection", conn);
        cmd.Parameters.AddWithValue("Collection", collection);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountAsync(string collection)
    {
        await using var conn = await _connectionFactory();

        await EnsureTableAsync(conn);

        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM vector_embeddings WHERE collection = @Collection", conn);
        cmd.Parameters.AddWithValue("Collection", collection);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static async Task EnsureTableAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS vector_embeddings (
                collection TEXT NOT NULL,
                item_id TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                embedding vector(1536),
                created_at TIMESTAMPTZ DEFAULT NOW(),
                PRIMARY KEY (collection, item_id, content_hash)
            )
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
