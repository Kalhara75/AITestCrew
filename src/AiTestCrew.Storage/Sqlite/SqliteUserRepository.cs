using System.Security.Cryptography;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IUserRepository"/>.
/// API keys are generated as 32-byte random hex strings prefixed with "atc_".
/// </summary>
public sealed class SqliteUserRepository : IUserRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteUserRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<User> CreateAsync(string name)
    {
        var user = new User
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name,
            ApiKey = GenerateApiKey(),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (id, name, api_key, created_at, is_active)
            VALUES ($id, $name, $apiKey, $createdAt, $isActive)
            """;
        cmd.Parameters.AddWithValue("$id", user.Id);
        cmd.Parameters.AddWithValue("$name", user.Name);
        cmd.Parameters.AddWithValue("$apiKey", user.ApiKey);
        cmd.Parameters.AddWithValue("$createdAt", user.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$isActive", 1);
        await cmd.ExecuteNonQueryAsync();
        return user;
    }

    public async Task<User?> GetByIdAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, api_key, created_at, is_active FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await ReadSingleAsync(cmd);
    }

    public async Task<User?> GetByApiKeyAsync(string apiKey)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, api_key, created_at, is_active FROM users WHERE api_key = $key";
        cmd.Parameters.AddWithValue("$key", apiKey);
        return await ReadSingleAsync(cmd);
    }

    public async Task<List<User>> ListAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, api_key, created_at, is_active FROM users ORDER BY created_at DESC";
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<User>();
        while (await reader.ReadAsync())
            result.Add(ReadUser(reader));
        return result;
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetActiveAsync(string id, bool isActive)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET is_active = $active WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<User?> ReadSingleAsync(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadUser(reader) : null;
    }

    private static User ReadUser(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        ApiKey = reader.GetString(2),
        CreatedAt = DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
        IsActive = reader.GetInt32(4) == 1
    };

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return $"atc_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}
