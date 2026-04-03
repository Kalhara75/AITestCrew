using AiTestCrew.Agents.ApiAgent;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Root envelope persisted to disk as a JSON file in the testsets/ directory.
/// One file per unique objective slug.
/// </summary>
public class PersistedTestSet
{
    /// <summary>Deterministic slug derived from the objective (e.g. "test-get-api-products-endpoint").</summary>
    public string Id { get; set; } = "";

    /// <summary>The original natural language objective used to generate this test set.</summary>
    public string Objective { get; set; } = "";

    /// <summary>UTC timestamp when this test set was first generated.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent execution (generate or reuse).</summary>
    public DateTime LastRunAt { get; set; }

    /// <summary>Total number of times this test set has been executed.</summary>
    public int RunCount { get; set; }

    /// <summary>One entry per decomposed task, each holding its generated test cases.</summary>
    public List<PersistedTaskEntry> Tasks { get; set; } = [];
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

    /// <summary>The exact test cases that were generated and should be replayed on reuse.</summary>
    public List<ApiTestCase> TestCases { get; set; } = [];
}
