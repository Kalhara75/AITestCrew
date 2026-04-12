using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.DesktopUiBase;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.WinFormsUiAgent;

/// <summary>
/// Tests Windows Forms desktop applications using FlaUI (UI Automation).
///
/// Configuration required (TestEnvironment section):
///   WinFormsAppPath                   — full path to the .exe under test
///   WinFormsAppArgs                   — (optional) command-line arguments
///   WinFormsAppLaunchTimeoutSeconds   — how long to wait for main window (default 30)
///   WinFormsScreenshotDir             — (optional) directory for failure screenshots
///   WinFormsCloseAppBetweenTests      — relaunch for clean state per test (default true)
/// </summary>
public class WinFormsUiTestAgent : BaseDesktopUiTestAgent
{
    public override string Name => "WinForms Desktop UI Agent";
    public override string Role =>
        "Senior Desktop UI Test Engineer specialising in Windows Forms applications. " +
        "You write thorough FlaUI-based UI tests covering happy path, error handling, " +
        "and boundary conditions. You use AutomationId and Name properties to identify " +
        "UI elements reliably.";

    protected override string TargetAppPath => _config.WinFormsAppPath;
    protected override string TargetAppArgs => _config.WinFormsAppArgs ?? "";
    protected override string TargetAppPathConfigKey => "WinFormsAppPath";

    public WinFormsUiTestAgent(
        Kernel kernel,
        ILogger<WinFormsUiTestAgent> logger,
        TestEnvironmentConfig config)
        : base(kernel, logger, config)
    {
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.UI_Desktop_WinForms);
}
