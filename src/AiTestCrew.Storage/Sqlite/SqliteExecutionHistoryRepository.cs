using System.Text.Json;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IExecutionHistoryRepository"/>.
/// Full run JSON is in the <c>data</c> column; indexed columns support listing/filtering.
/// </summary>
public sealed class SqliteExecutionHistoryRepository : IExecutionHistoryRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly int _maxRunsPerTestSet;

    public SqliteExecutionHistoryRepository(SqliteConnectionFactory factory, int maxRunsPerTestSet = 0)
    {
        _factory = factory;
        _maxRunsPerTestSet = maxRunsPerTestSet;
    }

    public async Task SaveAsync(PersistedExecutionRun run)
    {
        var json = JsonSerializer.Serialize(run, JsonOpts.Value);
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO execution_runs
                (run_id, test_set_id, module_id, status, data, started_at, completed_at)
            VALUES ($runId, $tsId, $modId, $status, $data, $startedAt, $completedAt)
            """;
        cmd.Parameters.AddWithValue("$runId", run.RunId);
        cmd.Parameters.AddWithValue("$tsId", run.TestSetId);
        cmd.Parameters.AddWithValue("$modId", (object?)run.ModuleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", run.Status);
        cmd.Parameters.AddWithValue("$data", json);
        cmd.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completedAt",
            run.CompletedAt.HasValue ? run.CompletedAt.Value.ToString("O") : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        await PruneOldRunsAsync(run.TestSetId);
    }

    public async Task<PersistedExecutionRun?> GetRunAsync(string testSetId, string runId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM execution_runs WHERE test_set_id = $tsId AND run_id = $runId";
        cmd.Parameters.AddWithValue("$tsId", testSetId);
        cmd.Parameters.AddWithValue("$runId", runId);
        var json = await cmd.ExecuteScalarAsync() as string;
        if (json is null) return null;
        var run = JsonSerializer.Deserialize<PersistedExecutionRun>(json, JsonOpts.Value);
        run?.MigrateToV2();
        return run;
    }

    public IReadOnlyList<PersistedExecutionRun> ListRuns(string testSetId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM execution_runs WHERE test_set_id = $tsId ORDER BY started_at DESC";
        cmd.Parameters.AddWithValue("$tsId", testSetId);
        using var reader = cmd.ExecuteReader();
        var result = new List<PersistedExecutionRun>();
        while (reader.Read())
        {
            var run = JsonSerializer.Deserialize<PersistedExecutionRun>(reader.GetString(0), JsonOpts.Value);
            if (run is not null)
            {
                run.MigrateToV2();
                result.Add(run);
            }
        }
        return result;
    }

    public PersistedExecutionRun? GetLatestRun(string testSetId)
    {
        var runs = ListRuns(testSetId);
        return runs.Count > 0 ? runs[0] : null;
    }

    public Dictionary<string, (PersistedObjectiveResult Result, string RunId)> GetLatestObjectiveStatuses(string testSetId)
    {
        var runs = ListRuns(testSetId);
        var result = new Dictionary<string, (PersistedObjectiveResult, string)>();
        foreach (var run in runs)
        {
            foreach (var obj in run.ObjectiveResults)
                result.TryAdd(obj.ObjectiveId, (obj, run.RunId));
        }
        return result;
    }

    public async Task DeleteRunsForTestSetAsync(string testSetId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM execution_runs WHERE test_set_id = $tsId";
        cmd.Parameters.AddWithValue("$tsId", testSetId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteRunAsync(string testSetId, string runId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM execution_runs WHERE test_set_id = $tsId AND run_id = $runId";
        cmd.Parameters.AddWithValue("$tsId", testSetId);
        cmd.Parameters.AddWithValue("$runId", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveObjectiveFromHistoryAsync(string testSetId, string objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT run_id, data FROM execution_runs WHERE test_set_id = $tsId";
        cmd.Parameters.AddWithValue("$tsId", testSetId);

        var updates = new List<(string RunId, string? NewJson)>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var runId = reader.GetString(0);
                var json = reader.GetString(1);
                var run = JsonSerializer.Deserialize<PersistedExecutionRun>(json, JsonOpts.Value);
                if (run is null) continue;
                run.MigrateToV2();

                var removed = run.ObjectiveResults.RemoveAll(r => r.ObjectiveId == objectiveId);
                if (removed == 0) continue;

                if (run.ObjectiveResults.Count == 0)
                {
                    updates.Add((runId, null)); // mark for deletion
                }
                else
                {
                    run.TotalObjectives = run.ObjectiveResults.Count;
                    run.PassedObjectives = run.ObjectiveResults.Count(r => r.Status == "Passed");
                    run.FailedObjectives = run.ObjectiveResults.Count(r => r.Status == "Failed");
                    run.ErrorObjectives = run.ObjectiveResults.Count(r => r.Status == "Error");
                    updates.Add((runId, JsonSerializer.Serialize(run, JsonOpts.Value)));
                }
            }
        }

        foreach (var (runId, newJson) in updates)
        {
            using var upd = conn.CreateCommand();
            if (newJson is null)
            {
                upd.CommandText = "DELETE FROM execution_runs WHERE run_id = $runId";
                upd.Parameters.AddWithValue("$runId", runId);
            }
            else
            {
                upd.CommandText = "UPDATE execution_runs SET data = $data WHERE run_id = $runId";
                upd.Parameters.AddWithValue("$runId", runId);
                upd.Parameters.AddWithValue("$data", newJson);
            }
            await upd.ExecuteNonQueryAsync();
        }
    }

    public Task<Dictionary<string, string>?> GetLatestDeliveryContextAsync(
        string testSetId, string? moduleId, string objectiveId)
    {
        foreach (var run in ListRuns(testSetId))
        {
            var match = run.ObjectiveResults
                .FirstOrDefault(o => string.Equals(o.ObjectiveId, objectiveId, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(o.Status, "Passed", StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            if (match.Deliveries is null || match.Deliveries.Count == 0) continue;

            var d = match.Deliveries[0];
            var filename = Path.GetFileName(d.RemotePath);
            var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MessageID"] = d.MessageId,
                ["TransactionID"] = d.TransactionId,
                ["Filename"] = filename,
                ["EndpointCode"] = d.EndpointCode,
                ["RemotePath"] = d.RemotePath,
                ["UploadedAs"] = d.UploadedAs,
            };
            return Task.FromResult<Dictionary<string, string>?>(ctx);
        }

        return Task.FromResult<Dictionary<string, string>?>(null);
    }

    public int CountRuns(string testSetId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM execution_runs WHERE test_set_id = $tsId";
        cmd.Parameters.AddWithValue("$tsId", testSetId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private async Task PruneOldRunsAsync(string testSetId)
    {
        if (_maxRunsPerTestSet <= 0) return;

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // Delete the oldest runs beyond the retention limit
        cmd.CommandText = """
            DELETE FROM execution_runs WHERE run_id IN (
                SELECT run_id FROM execution_runs
                WHERE test_set_id = $tsId
                ORDER BY started_at DESC
                LIMIT -1 OFFSET $keep
            )
            """;
        cmd.Parameters.AddWithValue("$tsId", testSetId);
        cmd.Parameters.AddWithValue("$keep", _maxRunsPerTestSet);
        await cmd.ExecuteNonQueryAsync();
    }
}
