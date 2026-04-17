using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// Creates and initialises SQLite connections with WAL mode enabled.
/// Shared by all SQLite repository implementations.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private bool _schemaEnsured;
    private readonly object _lock = new();

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;

        // Ensure the directory exists so SQLite can create the file
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.DataSource))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Opens a new connection. Ensures schema exists on first call.</summary>
    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        if (!_schemaEnsured)
        {
            lock (_lock)
            {
                if (!_schemaEnsured)
                {
                    DatabaseMigrator.EnsureSchema(conn);
                    _schemaEnsured = true;
                }
            }
        }

        return conn;
    }
}
