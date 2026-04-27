using AiTestCrew.Agents.Persistence;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Helpers for deciding whether a run must be dispatched to a local agent
/// (because the server can't execute browser / desktop UI tests in-process)
/// or can be executed in-process as before.
/// </summary>
public static class RunDispatchHelper
{
    /// <summary>Target types the server cannot execute — must be queued for a local agent.</summary>
    public static readonly HashSet<string> AgentOnlyTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "UI_Web_MVC",
        "UI_Web_Blazor",
        "UI_Desktop_WinForms"
    };

    public static bool RequiresAgent(string targetType) =>
        !string.IsNullOrEmpty(targetType) && AgentOnlyTargets.Contains(targetType);

    /// <summary>
    /// Inspects the test set (or a single objective within it) and returns the
    /// target type that requires an agent, if any. Returns null when the run
    /// can be executed in-process.
    ///
    /// Detects nested UI targets on ANY parent step type via the generalized
    /// <see cref="TestObjective.EnumerateAllPostSteps"/> walker — a delivery
    /// objective, a web UI case, a desktop UI case, etc. all need a local
    /// agent if any of their post-steps run against a browser / desktop surface.
    /// </summary>
    public static string? GetAgentRequiredTarget(PersistedTestSet testSet, string? objectiveId)
    {
        var objectives = string.IsNullOrEmpty(objectiveId)
            ? testSet.TestObjectives
            : testSet.TestObjectives.Where(o =>
                string.Equals(o.Id, objectiveId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Name, objectiveId, StringComparison.OrdinalIgnoreCase));

        foreach (var o in objectives)
        {
            if (RequiresAgent(o.TargetType)) return o.TargetType;

            foreach (var (_, _, _, postStep) in o.EnumerateAllPostSteps())
                if (RequiresAgent(postStep.Target)) return postStep.Target;
        }

        return null;
    }
}
