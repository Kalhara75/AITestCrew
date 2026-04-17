using System.Text.Json;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Reads and writes module manifests (module.json) inside the modules/ directory.
/// Each module is a subdirectory containing its manifest and zero or more test set JSON files.
/// </summary>
public class ModuleRepository : IModuleRepository
{
    private readonly string _modulesDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ModuleRepository(string baseDir)
    {
        _modulesDir = Path.Combine(baseDir, "modules");
        System.IO.Directory.CreateDirectory(_modulesDir);
    }

    /// <summary>Creates a new module directory with its manifest.</summary>
    public async Task<PersistedModule> CreateAsync(string name, string? description = null)
    {
        var id = SlugHelper.ToSlug(name);
        var moduleDir = Path.Combine(_modulesDir, id);
        System.IO.Directory.CreateDirectory(moduleDir);

        var module = new PersistedModule
        {
            Id = id,
            Name = name,
            Description = description ?? "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(module, JsonOpts);
        await File.WriteAllTextAsync(ManifestPath(id), json);
        return module;
    }

    /// <summary>Loads a module by its slug ID. Returns null if not found.</summary>
    public async Task<PersistedModule?> GetAsync(string moduleId)
    {
        var path = ManifestPath(moduleId);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<PersistedModule>(json, JsonOpts);
    }

    /// <summary>Lists all modules, ordered by creation date descending.</summary>
    public async Task<List<PersistedModule>> ListAllAsync()
    {
        var result = new List<PersistedModule>();
        if (!System.IO.Directory.Exists(_modulesDir)) return result;

        foreach (var dir in System.IO.Directory.EnumerateDirectories(_modulesDir))
        {
            var manifestPath = Path.Combine(dir, "module.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath);
                var module = JsonSerializer.Deserialize<PersistedModule>(json, JsonOpts);
                if (module is not null) result.Add(module);
            }
            catch { /* skip malformed */ }
        }

        return result.OrderByDescending(m => m.CreatedAt).ToList();
    }

    /// <summary>Updates an existing module manifest.</summary>
    public async Task UpdateAsync(PersistedModule module)
    {
        module.UpdatedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(module, JsonOpts);
        await File.WriteAllTextAsync(ManifestPath(module.Id), json);
    }

    /// <summary>Deletes a module directory. Fails if it still contains test set files.</summary>
    public Task DeleteAsync(string moduleId)
    {
        var moduleDir = ModuleDir(moduleId);
        if (!System.IO.Directory.Exists(moduleDir)) return Task.CompletedTask;

        var testSetFiles = System.IO.Directory.EnumerateFiles(moduleDir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("module.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (testSetFiles.Count > 0)
            throw new InvalidOperationException(
                $"Cannot delete module '{moduleId}' — it still contains {testSetFiles.Count} test set(s). Delete them first.");

        System.IO.Directory.Delete(moduleDir, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Checks whether a module directory exists.</summary>
    public bool Exists(string moduleId) => File.Exists(ManifestPath(moduleId));

    /// <summary>Returns the directory path for a given module.</summary>
    public string ModuleDir(string moduleId) => Path.Combine(_modulesDir, moduleId);

    /// <summary>The root modules directory.</summary>
    public string Directory => _modulesDir;

    private string ManifestPath(string moduleId) =>
        Path.Combine(_modulesDir, moduleId, "module.json");
}
