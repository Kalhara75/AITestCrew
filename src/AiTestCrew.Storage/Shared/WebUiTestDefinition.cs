using AiTestCrew.Agents.AseXmlAgent;

namespace AiTestCrew.Agents.Shared;

/// <summary>
/// The definition of a Web UI test — start URL, Playwright steps, and options.
/// Used inside <see cref="AiTestCrew.Agents.Persistence.TestObjective"/> for UI targets.
/// </summary>
public class WebUiTestDefinition
{
    /// <summary>Human-readable description of what this test verifies.</summary>
    public string Description { get; set; } = "";

    /// <summary>Starting URL for this test — relative or absolute.</summary>
    public string StartUrl { get; set; } = "";

    /// <summary>Ordered list of Playwright steps to execute.</summary>
    public List<WebUiStep> Steps { get; set; } = [];

    /// <summary>When true, a screenshot is saved on step failure.</summary>
    public bool TakeScreenshotOnFailure { get; set; } = true;

    /// <summary>
    /// Optional post-steps (sub-actions / sub-verifications) that run AFTER this
    /// web UI case completes. Each post-step targets a UI surface, API, aseXML
    /// delivery, or DB check and receives the parent case's context via
    /// <c>{{Token}}</c> substitution. Long waits queue for a remote agent via the
    /// shared <c>PostStepOrchestrator</c>; short waits run inline.
    /// </summary>
    public List<VerificationStep> PostSteps { get; set; } = [];

    /// <summary>
    /// Creates a <see cref="WebUiTestDefinition"/> from a legacy <see cref="WebUiTestCase"/>.
    /// </summary>
    public static WebUiTestDefinition FromTestCase(WebUiTestCase tc) => new()
    {
        Description = tc.Description,
        StartUrl = tc.StartUrl,
        Steps = tc.Steps,
        TakeScreenshotOnFailure = tc.TakeScreenshotOnFailure,
        PostSteps = tc.PostSteps
    };

    /// <summary>
    /// Creates a <see cref="WebUiTestCase"/> from this definition (for agent execution).
    /// </summary>
    public WebUiTestCase ToTestCase(string name) => new()
    {
        Name = name,
        Description = Description,
        StartUrl = StartUrl,
        Steps = Steps,
        TakeScreenshotOnFailure = TakeScreenshotOnFailure,
        PostSteps = PostSteps
    };
}
