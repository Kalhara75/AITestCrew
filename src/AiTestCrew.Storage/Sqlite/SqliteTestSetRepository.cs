using System.Text.Json;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="ITestSetRepository"/>.
/// Legacy (flat) test sets are stored with <c>module_id = ''</c>.
/// Full test set JSON is in the <c>data</c> column; indexed columns support listing/filtering.
/// </summary>
public sealed class SqliteTestSetRepository : ITestSetRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteTestSetRepository(SqliteConnectionFactory factory) => _factory = factory;

    // ── Legacy (flat) operations ──

    public async Task SaveAsync(PersistedTestSet testSet)
    {
        await UpsertAsync(testSet, moduleId: "");
    }

    public async Task<PersistedTestSet?> LoadAsync(string id)
    {
        return await LoadInternalAsync("", id);
    }

    public IReadOnlyList<PersistedTestSet> ListAll()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM test_sets ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        var result = new List<PersistedTestSet>();
        while (reader.Read())
        {
            var ts = Deserialize(reader.GetString(0));
            if (ts is not null) result.Add(ts);
        }
        return result;
    }

    public async Task UpdateRunStatsAsync(string id)
    {
        await UpdateRunStatsInternalAsync("", id);
    }

    // ── Module-scoped operations ──

    public async Task SaveAsync(PersistedTestSet testSet, string moduleId)
    {
        await UpsertAsync(testSet, moduleId);
    }

    public async Task<PersistedTestSet?> LoadAsync(string moduleId, string testSetId)
    {
        return await LoadInternalAsync(moduleId, testSetId);
    }

    public IReadOnlyList<PersistedTestSet> ListByModule(string moduleId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM test_sets WHERE module_id = $mid ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$mid", moduleId);
        using var reader = cmd.ExecuteReader();
        var result = new List<PersistedTestSet>();
        while (reader.Read())
        {
            var ts = Deserialize(reader.GetString(0));
            if (ts is not null) result.Add(ts);
        }
        return result;
    }

    public async Task<PersistedTestSet> CreateEmptyAsync(string moduleId, string name)
    {
        var id = SlugHelper.ToSlug(name);
        var testSet = new PersistedTestSet
        {
            Id = id,
            Name = name,
            ModuleId = moduleId,
            SchemaVersion = 2,
            CreatedAt = DateTime.UtcNow,
            LastRunAt = default,
            RunCount = 0,
            Objectives = [],
            TestObjectives = []
        };
        await UpsertAsync(testSet, moduleId);
        return testSet;
    }

    public async Task MergeObjectivesAsync(
        string moduleId, string testSetId,
        List<TestObjective> newObjectives, string objective,
        string? objectiveName = null,
        string? apiStackKey = null, string? apiModule = null,
        string? endpointCode = null,
        string? environmentKey = null)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var testSet = await LoadFromConnectionAsync(conn, moduleId, testSetId)
            ?? throw new InvalidOperationException(
                $"Test set '{testSetId}' not found in module '{moduleId}'.");

        // Add user objective if not already tracked
        if (!testSet.Objectives.Contains(objective, StringComparer.OrdinalIgnoreCase))
            testSet.Objectives.Add(objective);

        // Store or update the short display name
        if (!string.IsNullOrWhiteSpace(objectiveName))
            testSet.ObjectiveNames[objective] = objectiveName;

        // Merge objectives: match by ParentObjective text (replaces existing),
        // fall back to Id dedup for backward compatibility
        var existingByText = testSet.TestObjectives
            .Select((o, i) => (o, i))
            .Where(x => !string.IsNullOrEmpty(x.o.ParentObjective))
            .ToDictionary(x => x.o.ParentObjective, x => x.i, StringComparer.OrdinalIgnoreCase);
        var existingIds = testSet.TestObjectives.Select(o => o.Id).ToHashSet();

        foreach (var obj in newObjectives)
        {
            if (existingByText.TryGetValue(obj.ParentObjective, out var idx))
                testSet.TestObjectives[idx] = obj;
            else if (!existingIds.Contains(obj.Id))
                testSet.TestObjectives.Add(obj);
        }

        if (apiStackKey is not null) testSet.ApiStackKey = apiStackKey;
        if (apiModule is not null) testSet.ApiModule = apiModule;
        if (!string.IsNullOrWhiteSpace(endpointCode)) testSet.EndpointCode = endpointCode;
        if (!string.IsNullOrWhiteSpace(environmentKey)) testSet.EnvironmentKey = environmentKey;

        testSet.LastRunAt = DateTime.UtcNow;
        testSet.RunCount++;

        UpsertWithConnection(conn, testSet, moduleId);
        tx.Commit();
    }

    public async Task UpdateRunStatsAsync(string moduleId, string testSetId)
    {
        await UpdateRunStatsInternalAsync(moduleId, testSetId);
    }

    public async Task DeleteAsync(string moduleId, string testSetId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM test_sets WHERE module_id = $mid AND id = $id";
        cmd.Parameters.AddWithValue("$mid", moduleId);
        cmd.Parameters.AddWithValue("$id", testSetId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MoveObjectiveAsync(
        string sourceModuleId, string sourceTestSetId,
        string destModuleId, string destTestSetId,
        string objective)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var source = await LoadFromConnectionAsync(conn, sourceModuleId, sourceTestSetId)
            ?? throw new InvalidOperationException(
                $"Source test set '{sourceTestSetId}' not found in module '{sourceModuleId}'.");

        var objectivesToMove = source.TestObjectives
            .Where(o => string.Equals(o.ParentObjective, objective, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (objectivesToMove.Count == 0)
            throw new InvalidOperationException(
                $"No test objectives found for objective '{objective}' in test set '{sourceTestSetId}'.");

        source.ObjectiveNames.TryGetValue(objective, out var objectiveName);

        // Remove from source
        source.TestObjectives.RemoveAll(o =>
            string.Equals(o.ParentObjective, objective, StringComparison.OrdinalIgnoreCase));
        source.Objectives.RemoveAll(o =>
            string.Equals(o, objective, StringComparison.OrdinalIgnoreCase));
        source.ObjectiveNames.Remove(objective);

        if (source.TestObjectives.Count == 0 && source.Objectives.Count == 0)
        {
            DeleteWithConnection(conn, sourceModuleId, sourceTestSetId);
        }
        else
        {
            UpsertWithConnection(conn, source, sourceModuleId);
        }

        // Load destination and append
        var dest = await LoadFromConnectionAsync(conn, destModuleId, destTestSetId)
            ?? throw new InvalidOperationException(
                $"Destination test set '{destTestSetId}' not found in module '{destModuleId}'.");

        var existingIds = dest.TestObjectives.Select(o => o.Id).ToHashSet();
        foreach (var obj in objectivesToMove)
        {
            if (!existingIds.Contains(obj.Id))
                dest.TestObjectives.Add(obj);
        }

        if (!dest.Objectives.Contains(objective, StringComparer.OrdinalIgnoreCase))
            dest.Objectives.Add(objective);

        if (!string.IsNullOrWhiteSpace(objectiveName))
            dest.ObjectiveNames[objective] = objectiveName;

        // Inherit targeting metadata from source when destination is unset.
        // Prevents silent URL drift when objectives are moved into an empty
        // test set that has no ApiStackKey/ApiModule/EnvironmentKey/EndpointCode.
        if (string.IsNullOrWhiteSpace(dest.ApiStackKey))   dest.ApiStackKey   = source.ApiStackKey;
        if (string.IsNullOrWhiteSpace(dest.ApiModule))     dest.ApiModule     = source.ApiModule;
        if (string.IsNullOrWhiteSpace(dest.EnvironmentKey)) dest.EnvironmentKey = source.EnvironmentKey;
        if (string.IsNullOrWhiteSpace(dest.EndpointCode))  dest.EndpointCode  = source.EndpointCode;

        UpsertWithConnection(conn, dest, destModuleId);
        tx.Commit();
    }

    // ── Internal helpers ──

    private async Task<PersistedTestSet?> LoadInternalAsync(string moduleId, string testSetId)
    {
        using var conn = _factory.CreateConnection();
        return await LoadFromConnectionAsync(conn, moduleId, testSetId);
    }

    private static async Task<PersistedTestSet?> LoadFromConnectionAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn, string moduleId, string testSetId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM test_sets WHERE module_id = $mid AND id = $id";
        cmd.Parameters.AddWithValue("$mid", moduleId);
        cmd.Parameters.AddWithValue("$id", testSetId);
        var json = await cmd.ExecuteScalarAsync() as string;
        return json is null ? null : Deserialize(json);
    }

    private async Task UpsertAsync(PersistedTestSet testSet, string moduleId)
    {
        using var conn = _factory.CreateConnection();
        UpsertWithConnection(conn, testSet, moduleId);
        await Task.CompletedTask;
    }

    private static void UpsertWithConnection(
        Microsoft.Data.Sqlite.SqliteConnection conn, PersistedTestSet testSet, string moduleId)
    {
        var json = JsonSerializer.Serialize(testSet, JsonOpts.Value);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO test_sets (id, module_id, name, data, created_at, last_run_at, run_count)
            VALUES ($id, $mid, $name, $data, $createdAt, $lastRunAt, $runCount)
            ON CONFLICT (module_id, id) DO UPDATE SET
                name = excluded.name,
                data = excluded.data,
                last_run_at = excluded.last_run_at,
                run_count = excluded.run_count
            """;
        cmd.Parameters.AddWithValue("$id", testSet.Id);
        cmd.Parameters.AddWithValue("$mid", moduleId);
        cmd.Parameters.AddWithValue("$name", testSet.Name ?? "");
        cmd.Parameters.AddWithValue("$data", json);
        cmd.Parameters.AddWithValue("$createdAt", testSet.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$lastRunAt", testSet.LastRunAt == default ? "" : testSet.LastRunAt.ToString("O"));
        cmd.Parameters.AddWithValue("$runCount", testSet.RunCount);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteWithConnection(
        Microsoft.Data.Sqlite.SqliteConnection conn, string moduleId, string testSetId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM test_sets WHERE module_id = $mid AND id = $id";
        cmd.Parameters.AddWithValue("$mid", moduleId);
        cmd.Parameters.AddWithValue("$id", testSetId);
        cmd.ExecuteNonQuery();
    }

    private async Task UpdateRunStatsInternalAsync(string moduleId, string testSetId)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var testSet = await LoadFromConnectionAsync(conn, moduleId, testSetId);
        if (testSet is null) return;

        testSet.LastRunAt = DateTime.UtcNow;
        testSet.RunCount++;

        UpsertWithConnection(conn, testSet, moduleId);
        tx.Commit();
    }

    private static PersistedTestSet? Deserialize(string json)
    {
        var ts = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts.Value);
        if (ts is null) return null;
        ts.MigrateLegacyObjective();
        return ts;
    }
}
