namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Represents a test module — a top-level grouping of related test sets
/// (e.g. "Standing Data Replication (SDR)").
/// Stored as modules/{moduleId}/module.json.
/// </summary>
public class PersistedModule
{
    /// <summary>Slug identifier derived from the module name (e.g. "sdr").</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable display name (e.g. "Standing Data Replication (SDR)").</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional description of what this module covers.</summary>
    public string Description { get; set; } = "";

    /// <summary>UTC timestamp when this module was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp when this module was last modified.</summary>
    public DateTime UpdatedAt { get; set; }
}
