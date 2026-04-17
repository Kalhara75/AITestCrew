using System.Text.Json;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>Shared JSON serialization options for SQLite repositories.</summary>
internal static class JsonOpts
{
    internal static readonly JsonSerializerOptions Value = new()
    {
        WriteIndented = false,          // compact storage in DB
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
