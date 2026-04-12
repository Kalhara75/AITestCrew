using System.ComponentModel;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTestCrew.Agents.DesktopUiBase;

/// <summary>
/// Semantic Kernel plugin that gives the LLM live access to a desktop application's
/// UI Automation tree during test case generation. The LLM clicks, fills, and
/// inspects the real UI before generating test cases — so element identifiers
/// and observed values are based on direct observation, not guesswork.
///
/// Used only during generation; deterministic replay uses the stored DesktopUiTestCase JSON.
/// </summary>
public sealed class DesktopAutomationTools
{
    private readonly Application _app;
    private readonly UIA3Automation _automation;
    private readonly ILogger _logger;
    private readonly string? _screenshotDir;

    /// <summary>
    /// Every window state recorded by snapshot() — used by the generation phase to
    /// inject authoritative element identifiers so the LLM cannot hallucinate them.
    /// </summary>
    public record WindowObservation(string WindowTitle, string InteractiveElements);

    public List<WindowObservation> Observations { get; } = [];

    public DesktopAutomationTools(
        Application app, UIA3Automation automation, ILogger logger, string? screenshotDir)
    {
        _app           = app;
        _automation     = automation;
        _logger         = logger;
        _screenshotDir  = screenshotDir;
    }

    [KernelFunction("snapshot")]
    [Description(
        "Get the current window state: title, all interactive elements with their AutomationId, " +
        "Name, ControlType, and enabled/visible state. Call this after every click or fill to see " +
        "what changed and get accurate element identifiers for the next step.")]
    public string Snapshot()
    {
        _logger.LogDebug("[Desktop] snapshot");
        try
        {
            var window = GetMainWindow();
            var title = window.Title;

            var sb = new StringBuilder();
            sb.AppendLine($"Window Title: {title}");
            sb.AppendLine();
            sb.AppendLine("Interactive elements (use these EXACT identifiers in test steps):");

            var elements = window.FindAllDescendants();
            var interactiveCount = 0;
            var elementList = new StringBuilder();

            foreach (var el in elements)
            {
                if (interactiveCount >= 50) break;

                var controlType = el.Properties.ControlType.ValueOrDefault;

                // Only show interactive elements
                if (!IsInteractiveControlType(controlType)) continue;

                var automationId = el.Properties.AutomationId.ValueOrDefault ?? "";
                var name = el.Properties.Name.ValueOrDefault ?? "";
                var className = el.Properties.ClassName.ValueOrDefault ?? "";
                var isEnabled = el.Properties.IsEnabled.ValueOrDefault;
                var isOffscreen = el.Properties.IsOffscreen.ValueOrDefault;

                if (isOffscreen) continue; // Skip hidden elements

                var line = $"  [{controlType}] AutomationId=\"{automationId}\" " +
                           $"Name=\"{name}\" ClassName=\"{className}\" " +
                           $"Enabled={isEnabled}";

                // Add current value for text boxes
                if (controlType == ControlType.Edit && el.Patterns.Value.IsSupported)
                {
                    var val = el.Patterns.Value.Pattern.Value.Value ?? "";
                    if (!string.IsNullOrEmpty(val))
                        line += $" Value=\"{val[..Math.Min(60, val.Length)]}\"";
                }

                elementList.AppendLine(line);
                interactiveCount++;
            }

            var elemStr = elementList.ToString();
            sb.Append(elemStr);

            Observations.Add(new WindowObservation(title, elemStr));

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Snapshot failed: {ex.Message}";
        }
    }

    [KernelFunction("screenshot")]
    [Description(
        "Capture a screenshot of the current window and save it to disk. " +
        "Returns the file path. Use this to visually inspect the current state.")]
    public string Screenshot()
    {
        _logger.LogDebug("[Desktop] screenshot");
        try
        {
            var window = GetMainWindow();
            var image = Capture.Element(window);

            var dir = _screenshotDir ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"desktop-explore-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
            image.ToFile(path);

            return $"Screenshot saved: {path}";
        }
        catch (Exception ex)
        {
            return $"Screenshot failed: {ex.Message}";
        }
    }

    [KernelFunction("click")]
    [Description(
        "Click a desktop UI element by its AutomationId or Name. " +
        "The identifier must come from the most recent snapshot() output. " +
        "After clicking, call snapshot() to see the resulting state.")]
    public string Click(
        [Description("AutomationId of the element — copy exactly from snapshot")] string? automationId = null,
        [Description("Name of the element — use if AutomationId is empty")] string? name = null)
    {
        _logger.LogDebug("[Desktop] click → AutomationId={Id}, Name={Name}", automationId, name);
        try
        {
            var window = GetMainWindow();
            AutomationElement? element = null;

            if (!string.IsNullOrEmpty(automationId))
                element = window.FindFirstDescendant(window.ConditionFactory.ByAutomationId(automationId));

            if (element is null && !string.IsNullOrEmpty(name))
                element = window.FindFirstDescendant(window.ConditionFactory.ByName(name));

            if (element is null)
                return $"Click failed: element not found (AutomationId='{automationId}', Name='{name}'). " +
                       "Make sure you copied the identifier exactly from the snapshot.";

            element.Click();
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(300));

            return $"Clicked '{automationId ?? name}'. Call snapshot() to see the result.";
        }
        catch (Exception ex)
        {
            return $"Click failed: {ex.Message}";
        }
    }

    [KernelFunction("fill")]
    [Description(
        "Type text into an input field (Edit control). " +
        "The identifier must come from the most recent snapshot() output.")]
    public string Fill(
        [Description("AutomationId of the Edit control")] string? automationId = null,
        [Description("Name of the Edit control — use if AutomationId is empty")] string? name = null,
        [Description("Text to type into the field")] string value = "")
    {
        _logger.LogDebug("[Desktop] fill → AutomationId={Id}, Name={Name}, Value={Val}",
            automationId, name, value);
        try
        {
            var window = GetMainWindow();
            AutomationElement? element = null;

            if (!string.IsNullOrEmpty(automationId))
                element = window.FindFirstDescendant(window.ConditionFactory.ByAutomationId(automationId));

            if (element is null && !string.IsNullOrEmpty(name))
                element = window.FindFirstDescendant(window.ConditionFactory.ByName(name));

            if (element is null)
                return $"Fill failed: element not found (AutomationId='{automationId}', Name='{name}'). " +
                       "Make sure you copied the identifier exactly from the snapshot.";

            if (element.Patterns.Value.IsSupported)
            {
                element.Patterns.Value.Pattern.SetValue(value);
            }
            else
            {
                element.Focus();
                Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(100));
                Keyboard.Type(value);
            }

            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
            return $"Filled '{automationId ?? name}' with '{value}'.";
        }
        catch (Exception ex)
        {
            return $"Fill failed: {ex.Message}";
        }
    }

    [KernelFunction("list_windows")]
    [Description("List all top-level windows of the application with their titles.")]
    public string ListWindows()
    {
        _logger.LogDebug("[Desktop] list_windows");
        try
        {
            var windows = _app.GetAllTopLevelWindows(_automation);
            if (windows.Length == 0)
                return "No windows found.";

            var sb = new StringBuilder();
            sb.AppendLine("Application windows:");
            for (var i = 0; i < windows.Length; i++)
            {
                sb.AppendLine($"  {i + 1}. \"{windows[i].Title}\" (AutomationId=\"{windows[i].Properties.AutomationId.ValueOrDefault}\")");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"List windows failed: {ex.Message}";
        }
    }

    private Window GetMainWindow()
    {
        var windows = _app.GetAllTopLevelWindows(_automation);
        return windows.Length > 0
            ? windows[0]
            : throw new InvalidOperationException("Application has no visible windows");
    }

    private static bool IsInteractiveControlType(ControlType ct) =>
        ct == ControlType.Button ||
        ct == ControlType.Edit ||
        ct == ControlType.ComboBox ||
        ct == ControlType.CheckBox ||
        ct == ControlType.RadioButton ||
        ct == ControlType.List ||
        ct == ControlType.ListItem ||
        ct == ControlType.DataGrid ||
        ct == ControlType.DataItem ||
        ct == ControlType.Tab ||
        ct == ControlType.TabItem ||
        ct == ControlType.Menu ||
        ct == ControlType.MenuItem ||
        ct == ControlType.Tree ||
        ct == ControlType.TreeItem ||
        ct == ControlType.Hyperlink ||
        ct == ControlType.Slider ||
        ct == ControlType.Spinner ||
        ct == ControlType.ScrollBar;
}
