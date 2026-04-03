using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Reads and writes test sets as JSON files in the testsets/ directory.
/// Mirrors the pattern used by FileLoggerProvider for the logs/ directory.
/// </summary>
public class TestSetRepository
{
    private readonly string _dir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public TestSetRepository(string baseDir)
    {
        _dir = Path.Combine(baseDir, "testsets");
        System.IO.Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Converts a natural language objective into a deterministic file-safe slug.
    /// e.g. "Test GET /api/products endpoint" → "test-get-api-products-endpoint"
    /// </summary>
    public static string SlugFromObjective(string objective)
    {
        var lower = objective.ToLowerInvariant();
        // Replace any non-alphanumeric character with a hyphen
        var hyphenated = Regex.Replace(lower, @"[^a-z0-9]+", "-");
        // Collapse consecutive hyphens, trim ends
        var collapsed = Regex.Replace(hyphenated, @"-{2,}", "-").Trim('-');
        // Truncate at 80 chars on a word boundary
        if (collapsed.Length <= 80) return collapsed;
        var truncated = collapsed[..80];
        var lastHyphen = truncated.LastIndexOf('-');
        return lastHyphen > 0 ? truncated[..lastHyphen] : truncated;
    }

    /// <summary>
    /// Saves a test set to disk, overwriting any existing file with the same ID.
    /// </summary>
    public async Task SaveAsync(PersistedTestSet testSet)
    {
        var path = FilePath(testSet.Id);
        var json = JsonSerializer.Serialize(testSet, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Loads a test set by its slug ID. Returns null if not found.
    /// </summary>
    public async Task<PersistedTestSet?> LoadAsync(string id)
    {
        var path = FilePath(id);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
    }

    /// <summary>
    /// Lists all saved test sets, ordered by creation date descending.
    /// </summary>
    public IReadOnlyList<PersistedTestSet> ListAll()
    {
        var result = new List<PersistedTestSet>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var ts = JsonSerializer.Deserialize<PersistedTestSet>(json, JsonOpts);
                if (ts is not null) result.Add(ts);
            }
            catch { /* skip malformed files */ }
        }
        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    /// <summary>
    /// Increments RunCount and updates LastRunAt for an existing test set.
    /// </summary>
    public async Task UpdateRunStatsAsync(string id)
    {
        var testSet = await LoadAsync(id);
        if (testSet is null) return;
        testSet.LastRunAt = DateTime.UtcNow;
        testSet.RunCount++;
        await SaveAsync(testSet);
    }

    /// <summary>The absolute path for a given test set ID.</summary>
    public string FilePath(string id) => Path.Combine(_dir, $"{id}.json");

    /// <summary>Directory where test sets are stored.</summary>
    public string Directory => _dir;
}
