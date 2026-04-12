using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using AiTestCrew.Agents.Shared;

namespace AiTestCrew.Agents.DesktopUiBase;

/// <summary>
/// Resolves desktop UI elements using a cascading selector strategy:
///   1. AutomationId (most stable — maps to Control.Name in WinForms)
///   2. Name property (visible label/text)
///   3. ClassName + ControlType combination
///   4. TreePath (indexed walk from window root — least stable)
///
/// Supports retry/wait up to the step's timeout for elements that appear asynchronously
/// (e.g. after a dialog opens or data loads).
/// </summary>
public static class DesktopElementResolver
{
    /// <summary>
    /// Find an element in the given window using the composite selector from a <see cref="DesktopUiStep"/>.
    /// Retries until the element is found or the timeout expires.
    /// </summary>
    public static AutomationElement? FindElement(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var sw = Stopwatch.StartNew();
        var timeout = step.TimeoutMs;

        while (sw.ElapsedMilliseconds < timeout)
        {
            var element = TryFindElement(window, step, logger);
            if (element is not null)
                return element;

            Thread.Sleep(250);
        }

        // One last attempt
        return TryFindElement(window, step, logger);
    }

    private static AutomationElement? TryFindElement(Window window, DesktopUiStep step, ILogger logger)
    {
        var cf = window.ConditionFactory;

        // Priority 1: AutomationId
        if (!string.IsNullOrEmpty(step.AutomationId))
        {
            var el = window.FindFirstDescendant(cf.ByAutomationId(step.AutomationId));
            if (el is not null) return el;
            logger.LogDebug("AutomationId '{Id}' not found, trying next selector", step.AutomationId);
        }

        // Priority 2: Name
        if (!string.IsNullOrEmpty(step.Name))
        {
            var el = window.FindFirstDescendant(cf.ByName(step.Name));
            if (el is not null) return el;
            logger.LogDebug("Name '{Name}' not found, trying next selector", step.Name);
        }

        // Priority 3: ClassName + ControlType
        if (!string.IsNullOrEmpty(step.ClassName) && !string.IsNullOrEmpty(step.ControlType))
        {
            if (TryParseControlType(step.ControlType, out var ct))
            {
                var condition = new AndCondition(
                    cf.ByClassName(step.ClassName),
                    cf.ByControlType(ct));
                var el = window.FindFirstDescendant(condition);
                if (el is not null) return el;
            }
            logger.LogDebug("ClassName '{Class}' + ControlType '{CT}' not found, trying TreePath",
                step.ClassName, step.ControlType);
        }
        else if (!string.IsNullOrEmpty(step.ControlType))
        {
            // ControlType alone — useful when combined with Name fallback
            if (TryParseControlType(step.ControlType, out var ct))
            {
                if (!string.IsNullOrEmpty(step.Name))
                {
                    var condition = new AndCondition(
                        cf.ByControlType(ct),
                        cf.ByName(step.Name));
                    var el = window.FindFirstDescendant(condition);
                    if (el is not null) return el;
                }
            }
        }

        // Priority 4: TreePath
        if (!string.IsNullOrEmpty(step.TreePath))
        {
            var el = ResolveTreePath(window, step.TreePath);
            if (el is not null) return el;
            logger.LogDebug("TreePath '{Path}' not found", step.TreePath);
        }

        return null;
    }

    /// <summary>
    /// Walk the automation tree from the window root using an indexed path.
    /// Format: "Pane[0]/Button[2]" — each segment is ControlType[index].
    /// </summary>
    private static AutomationElement? ResolveTreePath(AutomationElement root, string treePath)
    {
        var segments = treePath.Split('/');
        AutomationElement? current = root;

        foreach (var segment in segments)
        {
            if (current is null) return null;

            var (typeName, index) = ParseTreeSegment(segment);
            if (typeName is null) return null;

            if (!TryParseControlType(typeName, out var controlType))
                return null;

            var children = current.FindAllChildren(
                current.ConditionFactory.ByControlType(controlType));

            if (index >= children.Length)
                return null;

            current = children[index];
        }

        return current;
    }

    private static (string? TypeName, int Index) ParseTreeSegment(string segment)
    {
        // Expected format: "Button[2]" or "Pane[0]"
        var bracketStart = segment.IndexOf('[');
        if (bracketStart < 0)
            return (segment, 0); // No index means [0]

        var typeName = segment[..bracketStart];
        var indexStr = segment[(bracketStart + 1)..].TrimEnd(']');

        return int.TryParse(indexStr, out var index)
            ? (typeName, index)
            : (typeName, 0);
    }

    private static bool TryParseControlType(string name, out ControlType controlType)
    {
        return Enum.TryParse(name, ignoreCase: true, out controlType);
    }

    /// <summary>
    /// Build the best available composite selector for a given automation element.
    /// Used by the recorder to produce stable selectors.
    /// </summary>
    public static DesktopUiStep BuildSelector(AutomationElement element, Window window)
    {
        var step = new DesktopUiStep();

        // AutomationId
        var automationId = element.Properties.AutomationId.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(automationId) && !IsAutoGeneratedId(automationId))
            step.AutomationId = automationId;

        // Name
        var name = element.Properties.Name.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(name))
            step.Name = name;

        // ClassName
        var className = element.Properties.ClassName.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(className))
            step.ClassName = className;

        // ControlType
        var controlType = element.Properties.ControlType.ValueOrDefault;
        step.ControlType = controlType.ToString();

        // TreePath — always build as fallback
        step.TreePath = BuildTreePath(element, window);

        return step;
    }

    /// <summary>
    /// Build an indexed path from the window root to the given element.
    /// </summary>
    public static string? BuildTreePath(AutomationElement element, Window window)
    {
        var segments = new Stack<string>();
        var current = element;

        while (current is not null && !Equals(current, window))
        {
            var parent = current.Parent;
            if (parent is null) break;

            var controlType = current.Properties.ControlType.ValueOrDefault;
            var siblings = parent.FindAllChildren(
                parent.ConditionFactory.ByControlType(controlType));

            var index = 0;
            for (var i = 0; i < siblings.Length; i++)
            {
                if (Equals(siblings[i], current))
                {
                    index = i;
                    break;
                }
            }

            segments.Push($"{controlType}[{index}]");
            current = parent;
        }

        return segments.Count > 0 ? string.Join("/", segments) : null;
    }

    /// <summary>
    /// Heuristic: skip auto-generated WinForms IDs that are unstable across runs.
    /// Covers: default type-based names (textBox1, button2), pure numeric IDs
    /// (window handles like "5049672"), and hex-looking IDs.
    /// </summary>
    private static bool IsAutoGeneratedId(string id) =>
        // Pure numeric — typically a runtime window handle, different every launch
        id.All(char.IsDigit) ||
        // Default WinForms designer names (type + number)
        id.StartsWith("textBox", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("button", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("label", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("comboBox", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("checkBox", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("radioButton", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("listBox", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("dataGridView", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("panel", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("groupBox", StringComparison.OrdinalIgnoreCase);

    private static bool Equals(AutomationElement a, AutomationElement b)
    {
        try
        {
            return a.Properties.NativeWindowHandle.ValueOrDefault ==
                   b.Properties.NativeWindowHandle.ValueOrDefault;
        }
        catch
        {
            return ReferenceEquals(a, b);
        }
    }
}
