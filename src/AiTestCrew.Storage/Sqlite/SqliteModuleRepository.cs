using System.Text.Json;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IModuleRepository"/>.
/// Stores the full <see cref="PersistedModule"/> JSON in a <c>data</c> column alongside
/// indexed columns used for queries.
/// </summary>
public sealed class SqliteModuleRepository : IModuleRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteModuleRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<PersistedModule> CreateAsync(string name, string? description = null)
    {
        var module = new PersistedModule
        {
            Id = SlugHelper.ToSlug(name),
            Name = name,
            Description = description ?? "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Version = 1
        };

        var json = JsonSerializer.Serialize(module, JsonOpts.Value);
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO modules (id, name, description, data, created_at, updated_at, version)
            VALUES ($id, $name, $desc, $data, $createdAt, $updatedAt, 1)
            """;
        cmd.Parameters.AddWithValue("$id", module.Id);
        cmd.Parameters.AddWithValue("$name", module.Name);
        cmd.Parameters.AddWithValue("$desc", module.Description);
        cmd.Parameters.AddWithValue("$data", json);
        cmd.Parameters.AddWithValue("$createdAt", module.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", module.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
        return module;
    }

    public async Task<PersistedModule?> GetAsync(string moduleId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data, version, updated_by FROM modules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", moduleId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var module = JsonSerializer.Deserialize<PersistedModule>(reader.GetString(0), JsonOpts.Value);
        if (module is null) return null;
        if (!reader.IsDBNull(1)) module.Version = reader.GetInt32(1);
        if (!reader.IsDBNull(2)) module.LastModifiedBy = reader.GetString(2);
        return module;
    }

    public async Task<List<PersistedModule>> ListAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM modules ORDER BY created_at DESC";
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<PersistedModule>();
        while (await reader.ReadAsync())
        {
            var json = reader.GetString(0);
            var module = JsonSerializer.Deserialize<PersistedModule>(json, JsonOpts.Value);
            if (module is not null) result.Add(module);
        }
        return result;
    }

    public async Task UpdateAsync(PersistedModule module)
    {
        module.UpdatedAt = DateTime.UtcNow;
        module.Version++;
        var json = JsonSerializer.Serialize(module, JsonOpts.Value);
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE modules SET name = $name, description = $desc, data = $data, updated_at = $updatedAt,
                version = $version, updated_by = $updatedBy
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", module.Id);
        cmd.Parameters.AddWithValue("$name", module.Name);
        cmd.Parameters.AddWithValue("$desc", module.Description);
        cmd.Parameters.AddWithValue("$data", json);
        cmd.Parameters.AddWithValue("$updatedAt", module.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$version", module.Version);
        cmd.Parameters.AddWithValue("$updatedBy", (object?)module.LastModifiedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string moduleId)
    {
        using var conn = _factory.CreateConnection();

        // Check for child test sets
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM test_sets WHERE module_id = $mid";
        countCmd.Parameters.AddWithValue("$mid", moduleId);
        var count = (long)(await countCmd.ExecuteScalarAsync() ?? 0);
        if (count > 0)
            throw new InvalidOperationException(
                $"Cannot delete module '{moduleId}' — it still contains {count} test set(s). Delete them first.");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM modules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", moduleId);
        await cmd.ExecuteNonQueryAsync();
    }

    public bool Exists(string moduleId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM modules WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", moduleId);
        return cmd.ExecuteScalar() is not null;
    }
}
