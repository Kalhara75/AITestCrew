using AiTestCrew.Agents.AseXmlAgent;

namespace AiTestCrew.Agents.Shared;

/// <summary>
/// The definition of a desktop UI test — steps and options.
/// Used inside <see cref="AiTestCrew.Agents.Persistence.TestObjective"/> for desktop targets.
/// </summary>
public class DesktopUiTestDefinition
{
    /// <summary>Human-readable description of what this test verifies.</summary>
    public string Description { get; set; } = "";

    /// <summary>Ordered list of desktop UI steps to execute.</summary>
    public List<DesktopUiStep> Steps { get; set; } = [];

    /// <summary>When true, a screenshot is saved on step failure.</summary>
    public bool TakeScreenshotOnFailure { get; set; } = true;

    /// <summary>
    /// Optional post-steps (sub-actions / sub-verifications) that run AFTER this
    /// desktop UI case completes. Each post-step targets a UI surface, API,
    /// aseXML delivery, or DB check and receives the parent case's context via
    /// <c>{{Token}}</c> substitution. Long waits queue for a remote agent.
    /// </summary>
    public List<VerificationStep> PostSteps { get; set; } = [];

    /// <summary>
    /// Creates a <see cref="DesktopUiTestDefinition"/> from a <see cref="DesktopUiTestCase"/>.
    /// </summary>
    public static DesktopUiTestDefinition FromTestCase(DesktopUiTestCase tc) => new()
    {
        Description = tc.Description,
        Steps = tc.Steps,
        TakeScreenshotOnFailure = tc.TakeScreenshotOnFailure,
        PostSteps = tc.PostSteps
    };

    /// <summary>
    /// Creates a <see cref="DesktopUiTestCase"/> from this definition (for agent execution).
    /// </summary>
    public DesktopUiTestCase ToTestCase(string name) => new()
    {
        Name = name,
        Description = Description,
        Steps = Steps,
        TakeScreenshotOnFailure = TakeScreenshotOnFailure,
        PostSteps = PostSteps
    };
}
