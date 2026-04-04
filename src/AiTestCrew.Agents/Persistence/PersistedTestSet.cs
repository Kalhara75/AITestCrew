using System.Text.Json.Serialization;
using AiTestCrew.Agents.ApiAgent;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Root envelope persisted to disk as a JSON file.
/// In module mode: modules/{moduleId}/{testSetId}.json
/// Legacy mode: testsets/{id}.json
/// A test set can accumulate test cases from multiple objectives over time.
/// </summary>
public class PersistedTestSet
{
    /// <summary>Deterministic slug used as the file name.</summary>
    public string Id { get; set; } = "";

    /// <summary>User-defined display name for the test set.</summary>
    public string Name { get; set; } = "";

    /// <summary>The module this test set belongs to (empty for legacy test sets).</summary>
    public string ModuleId { get; set; } = "";

    /// <summary>All objectives that have contributed test cases to this test set.</summary>
    public List<string> Objectives { get; set; } = [];

    /// <summary>
    /// The original/primary objective. For backward compatibility with legacy JSON files
    /// that have a single "objective" field. On deserialization of legacy files this gets
    /// populated; on new files it returns the first entry from <see cref="Objectives"/>.
    /// </summary>
    public string Objective
    {
        get => Objectives.Count > 0 ? Objectives[0] : _legacyObjective;
        set => _legacyObjective = value;
    }

    [JsonIgnore]
    private string _legacyObjective = "";

    /// <summary>UTC timestamp when this test set was first created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent execution (generate or reuse).</summary>
    public DateTime LastRunAt { get; set; }

    /// <summary>Total number of times this test set has been executed.</summary>
    public int RunCount { get; set; }

    /// <summary>
    /// Maps full objective text → short display name.
    /// Old JSON files without this field deserialize to an empty dictionary.
    /// </summary>
    public Dictionary<string, string> ObjectiveNames { get; set; } = new();

    /// <summary>One entry per decomposed task, each holding its generated test cases.</summary>
    public List<PersistedTaskEntry> Tasks { get; set; } = [];

    /// <summary>
    /// Returns the short display name for an objective if one exists,
    /// otherwise truncates the full text to ~60 characters.
    /// </summary>
    public string GetDisplayName(string objectiveText)
    {
        if (ObjectiveNames.TryGetValue(objectiveText, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        return objectiveText.Length <= 60
            ? objectiveText
            : string.Concat(objectiveText.AsSpan(0, 57), "...");
    }

    /// <summary>
    /// After deserializing a legacy file that only has "objective" (no "objectives" array),
    /// migrate the single value into the list so the rest of the code only needs to deal
    /// with <see cref="Objectives"/>.
    /// </summary>
    public void MigrateLegacyObjective()
    {
        if (Objectives.Count == 0 && !string.IsNullOrEmpty(_legacyObjective))
        {
            Objectives.Add(_legacyObjective);
        }

        // Backfill empty Objective on tasks created before per-objective tracking.
        // If there's only one objective, all tasks belong to it.
        // If there are multiple objectives, we can't guess — leave them empty.
        if (Objectives.Count == 1)
        {
            var obj = Objectives[0];
            foreach (var task in Tasks)
            {
                if (string.IsNullOrEmpty(task.Objective))
                    task.Objective = obj;
            }
        }
    }
}

/// <summary>
/// Holds the saved test cases for a single task within a <see cref="PersistedTestSet"/>.
/// </summary>
public class PersistedTaskEntry
{
    /// <summary>Original task ID (preserved so results can be correlated across runs).</summary>
    public string TaskId { get; set; } = "";

    /// <summary>Human-readable task description.</summary>
    public string TaskDescription { get; set; } = "";

    /// <summary>Name of the agent that executed this task.</summary>
    public string AgentName { get; set; } = "";

    /// <summary>The objective that produced this task (used for per-objective rebaseline).</summary>
    public string Objective { get; set; } = "";

    /// <summary>The exact test cases that were generated and should be replayed on reuse.</summary>
    public List<ApiTestCase> TestCases { get; set; } = [];
}
