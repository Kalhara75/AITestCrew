using System.Text.Json;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Reads and writes test sets as JSON files.
/// Supports both legacy flat layout (testsets/{id}.json) and
/// module-scoped layout (modules/{moduleId}/{testSetId}.json).
/// </summary>
public class TestSetRepository
{
    private readonly string _legacyDir;
    private readonly string _modulesDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public TestSetRepository(string baseDir)
    {
        _legacyDir = Path.Combine(baseDir, "testsets");
        _modulesDir = Path.Combine(baseDir, "modules");
        System.IO.Directory.CreateDirectory(_legacyDir);
    }

    // ─────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Converts a natural language objective into a deterministic file-safe slug.
    /// Delegates to <see cref="SlugHelper.ToSlug"/>.
    /// </summary>
    public static string SlugFromObjective(string objective) => SlugHelper.ToSlug(objective);

    // ─────────────────────────────────────────────────────
    // Legacy (flat) operations — testsets/{id}.json
    // ─────────────────────────────────────────────────────

    /// <summary>Saves a test set to the legacy testsets/ directory.</summary>
    public async Task SaveAsync(PersistedTestSet testSet)
    {
        var path = LegacyFilePath(testSet.Id);
        var json = JsonSerializer.Serialize(testSet, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>Loads a test set from the legacy testsets/ directory. Returns null if not found.</summary>
    public async Task<PersistedTestSet?> LoadAsync(string id)
    {
        var path = LegacyFilePath(id);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        var ts = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
        ts?.MigrateLegacyObjective();
        return ts;
    }

    /// <summary>
    /// Lists all saved test sets from both legacy and module directories,
    /// ordered by creation date descending.
    /// </summary>
    public IReadOnlyList<PersistedTestSet> ListAll()
    {
        var result = new List<PersistedTestSet>();

        // Legacy directory
        if (System.IO.Directory.Exists(_legacyDir))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_legacyDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var ts = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
                    if (ts is not null)
                    {
                        ts.MigrateLegacyObjective();
                        result.Add(ts);
                    }
                }
                catch { /* skip malformed */ }
            }
        }

        // Module directories
        if (System.IO.Directory.Exists(_modulesDir))
        {
            foreach (var moduleDir in System.IO.Directory.EnumerateDirectories(_modulesDir))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(moduleDir, "*.json"))
                {
                    if (Path.GetFileName(file).Equals("module.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var json = File.ReadAllText(file);
                        var ts = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
                        if (ts is not null)
                        {
                            ts.MigrateLegacyObjective();
                            result.Add(ts);
                        }
                    }
                    catch { /* skip malformed */ }
                }
            }
        }

        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    /// <summary>Increments RunCount and updates LastRunAt for an existing test set (legacy).</summary>
    public async Task UpdateRunStatsAsync(string id)
    {
        var testSet = await LoadAsync(id);
        if (testSet is null) return;
        testSet.LastRunAt = DateTime.UtcNow;
        testSet.RunCount++;
        await SaveAsync(testSet);
    }

    /// <summary>The absolute path for a given legacy test set ID.</summary>
    public string FilePath(string id) => LegacyFilePath(id);

    /// <summary>Legacy directory where flat test sets are stored.</summary>
    public string Directory => _legacyDir;

    private string LegacyFilePath(string id) => Path.Combine(_legacyDir, $"{id}.json");

    // ─────────────────────────────────────────────────────
    // Module-scoped operations — modules/{moduleId}/{id}.json
    // ─────────────────────────────────────────────────────

    /// <summary>Saves a test set within a module directory.</summary>
    public async Task SaveAsync(PersistedTestSet testSet, string moduleId)
    {
        var path = ModuleFilePath(moduleId, testSet.Id);
        var json = JsonSerializer.Serialize(testSet, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>Loads a test set from a module directory. Returns null if not found.</summary>
    public async Task<PersistedTestSet?> LoadAsync(string moduleId, string testSetId)
    {
        var path = ModuleFilePath(moduleId, testSetId);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        var ts = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
        ts?.MigrateLegacyObjective();
        return ts;
    }

    /// <summary>Lists all test sets within a specific module, ordered by creation date descending.</summary>
    public IReadOnlyList<PersistedTestSet> ListByModule(string moduleId)
    {
        var result = new List<PersistedTestSet>();
        var moduleDir = Path.Combine(_modulesDir, moduleId);
        if (!System.IO.Directory.Exists(moduleDir)) return result;

        foreach (var file in System.IO.Directory.EnumerateFiles(moduleDir, "*.json"))
        {
            if (Path.GetFileName(file).Equals("module.json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var json = File.ReadAllText(file);
                var ts = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
                if (ts is not null)
                {
                    ts.MigrateLegacyObjective();
                    result.Add(ts);
                }
            }
            catch { /* skip malformed */ }
        }

        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    /// <summary>Creates an empty test set within a module (pre-creation before objectives are run).</summary>
    public async Task<PersistedTestSet> CreateEmptyAsync(string moduleId, string name)
    {
        var id = SlugHelper.ToSlug(name);
        var testSet = new PersistedTestSet
        {
            Id = id,
            Name = name,
            ModuleId = moduleId,
            CreatedAt = DateTime.UtcNow,
            LastRunAt = default,
            RunCount = 0,
            Objectives = [],
            Tasks = []
        };
        await SaveAsync(testSet, moduleId);
        return testSet;
    }

    /// <summary>
    /// Merges new task entries into an existing test set within a module.
    /// Appends tasks (deduplicating by TaskId) and adds the objective if not already present.
    /// </summary>
    public async Task MergeTasksAsync(
        string moduleId, string testSetId,
        List<PersistedTaskEntry> newTasks, string objective,
        string? objectiveName = null)
    {
        var testSet = await LoadAsync(moduleId, testSetId)
            ?? throw new InvalidOperationException(
                $"Test set '{testSetId}' not found in module '{moduleId}'.");

        // Add objective if not already tracked
        if (!testSet.Objectives.Contains(objective, StringComparer.OrdinalIgnoreCase))
            testSet.Objectives.Add(objective);

        // Store or update the short display name
        if (!string.IsNullOrWhiteSpace(objectiveName))
            testSet.ObjectiveNames[objective] = objectiveName;

        // Append tasks, deduplicating by TaskId
        var existingIds = testSet.Tasks.Select(t => t.TaskId).ToHashSet();
        foreach (var task in newTasks)
        {
            if (!existingIds.Contains(task.TaskId))
                testSet.Tasks.Add(task);
        }

        testSet.LastRunAt = DateTime.UtcNow;
        testSet.RunCount++;
        await SaveAsync(testSet, moduleId);
    }

    /// <summary>Increments RunCount and updates LastRunAt for a module-scoped test set.</summary>
    public async Task UpdateRunStatsAsync(string moduleId, string testSetId)
    {
        var testSet = await LoadAsync(moduleId, testSetId);
        if (testSet is null) return;
        testSet.LastRunAt = DateTime.UtcNow;
        testSet.RunCount++;
        await SaveAsync(testSet, moduleId);
    }

    /// <summary>Deletes a test set file from a module directory.</summary>
    public Task DeleteAsync(string moduleId, string testSetId)
    {
        var path = ModuleFilePath(moduleId, testSetId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves all tasks belonging to an objective from one test set to another (cross-module).
    /// Removes the objective and its tasks from the source, appends to the destination.
    /// </summary>
    public async Task MoveObjectiveAsync(
        string sourceModuleId, string sourceTestSetId,
        string destModuleId, string destTestSetId,
        string objective)
    {
        // Load source
        var source = await LoadAsync(sourceModuleId, sourceTestSetId)
            ?? throw new InvalidOperationException(
                $"Source test set '{sourceTestSetId}' not found in module '{sourceModuleId}'.");

        // Extract tasks belonging to the objective
        var tasksToMove = source.Tasks
            .Where(t => string.Equals(t.Objective, objective, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tasksToMove.Count == 0)
            throw new InvalidOperationException(
                $"No tasks found for objective '{objective}' in test set '{sourceTestSetId}'.");

        // Capture the short display name before removing from source
        source.ObjectiveNames.TryGetValue(objective, out var objectiveName);

        // Remove from source
        source.Tasks.RemoveAll(t =>
            string.Equals(t.Objective, objective, StringComparison.OrdinalIgnoreCase));
        source.Objectives.RemoveAll(o =>
            string.Equals(o, objective, StringComparison.OrdinalIgnoreCase));
        source.ObjectiveNames.Remove(objective);

        // Save or delete source
        if (source.Tasks.Count == 0 && source.Objectives.Count == 0)
            await DeleteAsync(sourceModuleId, sourceTestSetId);
        else
            await SaveAsync(source, sourceModuleId);

        // Load destination
        var dest = await LoadAsync(destModuleId, destTestSetId)
            ?? throw new InvalidOperationException(
                $"Destination test set '{destTestSetId}' not found in module '{destModuleId}'.");

        // Append tasks (dedup by TaskId)
        var existingIds = dest.Tasks.Select(t => t.TaskId).ToHashSet();
        foreach (var task in tasksToMove)
        {
            if (!existingIds.Contains(task.TaskId))
                dest.Tasks.Add(task);
        }

        // Add objective if not present
        if (!dest.Objectives.Contains(objective, StringComparer.OrdinalIgnoreCase))
            dest.Objectives.Add(objective);

        // Carry the short display name to the destination
        if (!string.IsNullOrWhiteSpace(objectiveName))
            dest.ObjectiveNames[objective] = objectiveName;

        await SaveAsync(dest, destModuleId);
    }

    private string ModuleFilePath(string moduleId, string testSetId) =>
        Path.Combine(_modulesDir, moduleId, $"{testSetId}.json");
}
