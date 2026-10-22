using Npgsql;

namespace Ava.Api.Data;

/// <summary>
/// Lightweight connection factory for Npgsql. Registered as singleton.
/// Uses NpgsqlDataSource for efficient connection pooling.
/// </summary>
public class DbConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public DbConnectionFactory(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    public NpgsqlConnection CreateConnection() => _dataSource.CreateConnection();

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = _dataSource.CreateConnection();
        await conn.OpenAsync();
        return conn;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
