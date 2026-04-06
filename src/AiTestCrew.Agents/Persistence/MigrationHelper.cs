using System.Text.Json;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Migrates persisted data between schema versions.
/// All methods are idempotent — safe to call at every startup.
/// </summary>
public static class MigrationHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Migrates existing test sets from testsets/ into a "Default" module.
    /// Safe to call at every startup — will no-op if already migrated.
    /// </summary>
    public static async Task MigrateToModulesAsync(string dataDir)
    {
        var legacyDir = Path.Combine(dataDir, "testsets");
        var modulesDir = Path.Combine(dataDir, "modules");
        var defaultModuleDir = Path.Combine(modulesDir, "default");
        var manifestPath = Path.Combine(defaultModuleDir, "module.json");

        // If the default module already exists, migration has already run
        if (File.Exists(manifestPath)) return;

        // If there are no legacy test sets, nothing to migrate — just ensure modules/ exists
        if (!Directory.Exists(legacyDir) || !Directory.EnumerateFiles(legacyDir, "*.json").Any())
        {
            Directory.CreateDirectory(modulesDir);
            return;
        }

        // Create the default module
        Directory.CreateDirectory(defaultModuleDir);
        var module = new PersistedModule
        {
            Id = "default",
            Name = "Default",
            Description = "Auto-migrated test sets from the legacy flat directory.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var moduleJson = JsonSerializer.Serialize(module, JsonOpts);
        await File.WriteAllTextAsync(manifestPath, moduleJson);

        // Copy each legacy test set into the default module
        foreach (var file in Directory.EnumerateFiles(legacyDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var testSet = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
                if (testSet is null) continue;

                testSet.ModuleId = "default";
                testSet.MigrateLegacyObjective();
                if (string.IsNullOrEmpty(testSet.Name))
                    testSet.Name = testSet.Objective;

                var destPath = Path.Combine(defaultModuleDir, Path.GetFileName(file));
                var updatedJson = JsonSerializer.Serialize(testSet, JsonOpts);
                await File.WriteAllTextAsync(destPath, updatedJson);
            }
            catch
            {
                // Skip malformed files
            }
        }
    }

    /// <summary>
    /// Migrates test sets and execution history from v1 (Task-based) to v2 (Objective-based) schema.
    /// Idempotent — files already at v2 are skipped.
    /// </summary>
    public static async Task MigrateToSchemaV2Async(string dataDir)
    {
        var modulesDir = Path.Combine(dataDir, "modules");
        var legacyDir = Path.Combine(dataDir, "testsets");
        var executionsDir = Path.Combine(dataDir, "executions");

        // Migrate test sets in modules/
        if (Directory.Exists(modulesDir))
        {
            foreach (var moduleDir in Directory.EnumerateDirectories(modulesDir))
            {
                foreach (var file in Directory.EnumerateFiles(moduleDir, "*.json"))
                {
                    if (Path.GetFileName(file).Equals("module.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    await MigrateTestSetFileAsync(file);
                }
            }
        }

        // Migrate legacy test sets in testsets/
        if (Directory.Exists(legacyDir))
        {
            foreach (var file in Directory.EnumerateFiles(legacyDir, "*.json"))
            {
                await MigrateTestSetFileAsync(file);
            }
        }

        // Migrate execution history
        if (Directory.Exists(executionsDir))
        {
            foreach (var tsDir in Directory.EnumerateDirectories(executionsDir))
            {
                foreach (var file in Directory.EnumerateFiles(tsDir, "*.json"))
                {
                    await MigrateExecutionRunFileAsync(file);
                }
            }
        }
    }

    private static async Task MigrateTestSetFileAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);

            // Quick check: if already v2, skip
            if (json.Contains("\"schemaVersion\": 2") || json.Contains("\"schemaVersion\":2"))
                return;

            var testSet = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
            if (testSet is null) return;

            // MigrateLegacyObjective handles in-memory v1→v2 conversion
            testSet.MigrateLegacyObjective();

            if (testSet.SchemaVersion < 2)
                testSet.SchemaVersion = 2;

            // Write back with v2 schema
            var updatedJson = JsonSerializer.Serialize(testSet, JsonOpts);
            await File.WriteAllTextAsync(filePath, updatedJson);
        }
        catch
        {
            // Skip malformed files
        }
    }

    private static async Task MigrateExecutionRunFileAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);

            // Quick check: if already v2, skip
            if (json.Contains("\"schemaVersion\": 2") || json.Contains("\"schemaVersion\":2"))
                return;

            var run = JsonSerializer.Deserialize<PersistedExecutionRun>(json, JsonOpts);
            if (run is null) return;

            run.MigrateToV2();

            var updatedJson = JsonSerializer.Serialize(run, JsonOpts);
            await File.WriteAllTextAsync(filePath, updatedJson);
        }
        catch
        {
            // Skip malformed files
        }
    }
}
