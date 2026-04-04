using System.Text.Json;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Migrates legacy flat testsets/ files into the modules/ directory structure.
/// Idempotent — skips if modules/ already contains data.
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
}
