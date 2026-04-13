using Microsoft.Extensions.Logging;
using AiTestCrew.Agents.Shared;

namespace AiTestCrew.Agents.AseXmlAgent.Recording;

/// <summary>
/// Post-processes a freshly recorded <see cref="WebUiTestCase"/> / <see cref="DesktopUiTestCase"/>
/// by replacing captured literal values with <c>{{Key}}</c> placeholders, using the
/// context that the delivery-side agent will provide at playback time.
///
/// Design choices (calibrated to keep false positives low):
/// <list type="bullet">
///   <item><description><b>Min length 4</b> — short values like checksums or durations are left alone.</description></item>
///   <item><description><b>Longest match first</b> — sort context by value length desc so a MessageID replaces whole before any substring collides.</description></item>
///   <item><description><b>Exact substring only</b> — no regex / no word-boundary tricks. Replaces <c>4103035611</c> with <c>{{NMI}}</c> but won't touch <c>4103035611a</c>.</description></item>
///   <item><description><b>First key wins</b> on value collisions; a WARN is logged.</description></item>
/// </list>
/// </summary>
public static class VerificationRecorderHelper
{
    private const int MinParameteriseLength = 4;

    public static void AutoParameteriseWebUi(
        WebUiTestCase testCase,
        IReadOnlyDictionary<string, string> context,
        ILogger? logger = null)
    {
        var table = BuildParameteriseTable(context, logger);
        if (table.Count == 0) return;

        testCase.StartUrl = ApplyAll(testCase.StartUrl, table);
        foreach (var step in testCase.Steps)
        {
            step.Selector = ApplyAll(step.Selector, table);
            step.Value    = ApplyAll(step.Value,    table);
        }
    }

    public static void AutoParameteriseDesktopUi(
        DesktopUiTestCase testCase,
        IReadOnlyDictionary<string, string> context,
        ILogger? logger = null)
    {
        var table = BuildParameteriseTable(context, logger);
        if (table.Count == 0) return;

        foreach (var step in testCase.Steps)
        {
            step.AutomationId = ApplyAll(step.AutomationId, table);
            step.Name         = ApplyAll(step.Name,         table);
            step.ClassName    = ApplyAll(step.ClassName,    table);
            step.ControlType  = ApplyAll(step.ControlType,  table);
            step.TreePath     = ApplyAll(step.TreePath,     table);
            step.Value        = ApplyAll(step.Value,        table);
            step.MenuPath     = ApplyAll(step.MenuPath,     table);
            step.WindowTitle  = ApplyAll(step.WindowTitle,  table);
        }
    }

    // ──────────────────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ordered list of (literalValue, placeholder) pairs, longest literal first,
    /// first-key-wins on collisions. Also filters out values below the minimum length.
    /// </summary>
    private static List<(string Literal, string Placeholder)> BuildParameteriseTable(
        IReadOnlyDictionary<string, string> context, ILogger? logger)
    {
        var seenLiterals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, literal) in context)
        {
            if (string.IsNullOrEmpty(literal)) continue;
            if (literal.Length < MinParameteriseLength) continue;

            if (seenLiterals.TryGetValue(literal, out var existingKey))
            {
                logger?.LogWarning(
                    "Auto-parameterise: value '{Literal}' matched by multiple context keys " +
                    "('{First}' and '{Second}'). Using '{First}'.",
                    literal, existingKey, key, existingKey);
                continue;  // keep first-seen mapping
            }
            seenLiterals[literal] = key;
        }

        return seenLiterals
            .OrderByDescending(kvp => kvp.Key.Length)
            .Select(kvp => (kvp.Key, "{{" + kvp.Value + "}}"))
            .ToList();
    }

    private static string? ApplyAll(string? input, List<(string Literal, string Placeholder)> table)
    {
        if (string.IsNullOrEmpty(input)) return input;
        foreach (var (literal, placeholder) in table)
        {
            if (input!.Contains(literal, StringComparison.Ordinal))
                input = input.Replace(literal, placeholder, StringComparison.Ordinal);
        }
        return input;
    }
}
