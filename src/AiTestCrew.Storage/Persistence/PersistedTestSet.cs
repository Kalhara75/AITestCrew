using System.Text.Json.Serialization;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.Shared;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Root envelope persisted to disk as a JSON file.
/// In module mode: modules/{moduleId}/{testSetId}.json
/// Legacy mode: testsets/{id}.json
/// A test set can accumulate test objectives from multiple user objectives over time.
/// </summary>
public class PersistedTestSet
{
    /// <summary>Deterministic slug used as the file name.</summary>
    public string Id { get; set; } = "";

    /// <summary>User-defined display name for the test set.</summary>
    public string Name { get; set; } = "";

    /// <summary>The module this test set belongs to (empty for legacy test sets).</summary>
    public string ModuleId { get; set; } = "";

    /// <summary>API stack key this test set targets (e.g. "bravecloud", "legacy"). Null = legacy flat config.</summary>
    public string? ApiStackKey { get; set; }

    /// <summary>API module key this test set targets (e.g. "sdr", "security"). Null = legacy flat config.</summary>
    public string? ApiModule { get; set; }

    /// <summary>
    /// Bravo delivery endpoint code this test set targets (e.g. "GatewaySPARQ").
    /// Null when the test set isn't a delivery test set. Mirrors how
    /// <see cref="ApiStackKey"/>/<see cref="ApiModule"/> persist per-test-set targeting.
    /// </summary>
    public string? EndpointCode { get; set; }

    /// <summary>All user objectives that have contributed test objectives to this test set.</summary>
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

    /// <summary>User ID who created this test set. Null for test sets created before user tracking.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>User ID who last modified this test set.</summary>
    public string? LastModifiedBy { get; set; }

    /// <summary>
    /// Maps full objective text → short display name.
    /// Old JSON files without this field deserialize to an empty dictionary.
    /// </summary>
    public Dictionary<string, string> ObjectiveNames { get; set; } = new();

    /// <summary>
    /// Schema version for migration detection.
    /// Version 1 (or absent): legacy format with Tasks containing test cases.
    /// Version 2: new format with flat TestObjectives list.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Starting URL for setup steps (e.g. the login page).
    /// Empty string means setup steps execute on whatever page the browser opens to.
    /// </summary>
    public string SetupStartUrl { get; set; } = "";

    /// <summary>
    /// Optional setup steps (e.g. login) that run before every test case in this test set.
    /// Recorded via PlaywrightRecorder or defined manually. Empty list = no setup.
    /// </summary>
    public List<WebUiStep> SetupSteps { get; set; } = [];

    /// <summary>Flat list of individually runnable test objectives (v2 schema).</summary>
    public List<TestObjective> TestObjectives { get; set; } = [];

    /// <summary>
    /// Legacy task entries — populated only when deserializing v1 JSON files.
    /// Null/empty in v2 files. Kept for migration deserialization.
    /// </summary>
    [Obsolete("Use TestObjectives instead. Tasks is only used for v1 migration.")]
    public List<PersistedTaskEntry>? Tasks { get; set; }

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
    /// Also performs in-memory v1→v2 migration if Tasks is populated but TestObjectives is empty.
    /// </summary>
    public void MigrateLegacyObjective()
    {
        if (Objectives.Count == 0 && !string.IsNullOrEmpty(_legacyObjective))
        {
            Objectives.Add(_legacyObjective);
        }

#pragma warning disable CS0612 // Type or member is obsolete
        // Backfill empty Objective on tasks created before per-objective tracking.
        if (Tasks is { Count: > 0 } && Objectives.Count == 1)
        {
            var obj = Objectives[0];
            foreach (var task in Tasks)
            {
                if (string.IsNullOrEmpty(task.Objective))
                    task.Objective = obj;
            }
        }

        // In-memory v1→v2: promote Tasks into TestObjectives if not already migrated
        if (SchemaVersion < 2 && Tasks is { Count: > 0 } && TestObjectives.Count == 0)
        {
            TestObjectives = MigrateTasksToObjectives(Tasks);
            SchemaVersion = 2;
        }
#pragma warning restore CS0612

        // Backfill Source for objectives created before Source tracking
        foreach (var obj in TestObjectives)
        {
            if (string.IsNullOrEmpty(obj.Source))
                obj.Source = obj.Id.StartsWith("recorded-", StringComparison.Ordinal)
                    ? "Recorded"
                    : "Generated";
        }
    }

    /// <summary>
    /// Converts legacy PersistedTaskEntry list into TestObjective list.
    /// Groups tasks by user objective — all test cases from tasks sharing the same
    /// objective text become steps within a single TestObjective.
    /// </summary>
    internal static List<TestObjective> MigrateTasksToObjectives(List<PersistedTaskEntry> tasks)
    {
        var objectives = new List<TestObjective>();

        // Group tasks by their user objective text
        var grouped = tasks.GroupBy(t => t.Objective, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var objectiveText = group.Key;
            var apiSteps = new List<ApiTestDefinition>();
            var webUiSteps = new List<WebUiTestDefinition>();
            var agentName = group.First().AgentName;
            var targetType = group.First().TargetType;

            foreach (var task in group)
            {
                foreach (var tc in task.TestCases)
                    apiSteps.Add(ApiTestDefinition.FromTestCase(tc));

                foreach (var tc in task.WebUiTestCases)
                    webUiSteps.Add(WebUiTestDefinition.FromTestCase(tc));

                // Use the most specific agent/target from tasks in this group
                if (!string.IsNullOrEmpty(task.AgentName))
                    agentName = task.AgentName;
                if (!string.IsNullOrEmpty(task.TargetType))
                    targetType = task.TargetType;
            }

            objectives.Add(new TestObjective
            {
                Id = SlugHelper.ToSlug(objectiveText),
                Name = objectiveText.Length <= 80
                    ? objectiveText
                    : string.Concat(objectiveText.AsSpan(0, 77), "..."),
                ParentObjective = objectiveText,
                AgentName = agentName,
                TargetType = targetType,
                ApiSteps = apiSteps,
                WebUiSteps = webUiSteps
            });
        }

        return objectives;
    }
}

/// <summary>
/// Legacy: holds saved test cases for a single task within a test set (v1 schema).
/// Kept for deserialization of legacy JSON files during migration.
/// </summary>
[Obsolete("Use TestObjective instead. PersistedTaskEntry is only used for v1 migration.")]
public class PersistedTaskEntry
{
    /// <summary>Original task ID.</summary>
    public string TaskId { get; set; } = "";

    /// <summary>Human-readable task description.</summary>
    public string TaskDescription { get; set; } = "";

    /// <summary>Name of the agent that executed this task.</summary>
    public string AgentName { get; set; } = "";

    /// <summary>The objective that produced this task.</summary>
    public string Objective { get; set; } = "";

    /// <summary>API test cases (legacy).</summary>
    public List<ApiTestCase> TestCases { get; set; } = [];

    /// <summary>Web UI test cases (legacy).</summary>
    public List<WebUiTestCase> WebUiTestCases { get; set; } = [];

    /// <summary>Target type string, defaults to "API_REST".</summary>
    public string TargetType { get; set; } = "API_REST";
}
