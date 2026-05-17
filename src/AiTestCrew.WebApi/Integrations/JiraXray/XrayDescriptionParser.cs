using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTestCrew.WebApi.Integrations.JiraXray;

/// <summary>
/// Parses a Jira Description field (ADF JSON or plain text/wiki markup) into structured sections.
/// Section names are matched case-insensitively with common variants.
/// </summary>
public static class XrayDescriptionParser
{
    private static readonly string[] PreconditionNames =
        ["preconditions", "pre-conditions", "precondition", "setup"];

    private static readonly string[] TestDataNames =
        ["test data", "data", "inputs"];

    private static readonly string[] OutcomeNames =
        ["expected outcome", "expected outcomes", "expected result", "expected results",
         "acceptance criteria", "verifications"];

    /// <summary>
    /// Parse the raw description string into structured sections.
    /// Returns null for empty descriptions.
    /// </summary>
    public static ParsedXrayDescription? Parse(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        if (description.TrimStart().StartsWith('{'))
        {
            try
            {
                var doc = JsonDocument.Parse(description);
                return ParseAdf(doc.RootElement);
            }
            catch (JsonException) { }
        }

        return ParsePlainText(description);
    }

    private static ParsedXrayDescription ParseAdf(JsonElement root)
    {
        var lines = new List<(string? Section, string Text, bool IsBullet)>();
        if (root.TryGetProperty("content", out var content))
            ExtractAdfContent(content, lines);
        return SectioniseLines(lines);
    }

    private static void ExtractAdfContent(
        JsonElement content,
        List<(string? Section, string Text, bool IsBullet)> lines)
    {
        foreach (var node in content.EnumerateArray())
        {
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "heading")
            {
                // heading text becomes the section name marker
                lines.Add((ExtractTextFromNode(node), string.Empty, false));
                continue;
            }

            if (type == "paragraph")
            {
                var text = ExtractTextFromNode(node);
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add((null, text, false));
                continue;
            }

            if (type is "bulletList" or "orderedList")
            {
                if (node.TryGetProperty("content", out var listContent))
                    foreach (var item in listContent.EnumerateArray())
                    {
                        var itemText = ExtractTextFromNode(item);
                        if (!string.IsNullOrWhiteSpace(itemText))
                            lines.Add((null, itemText.Trim(), true));
                    }
                continue;
            }

            if (node.TryGetProperty("content", out var child))
                ExtractAdfContent(child, lines);
        }
    }

    private static string ExtractTextFromNode(JsonElement node)
    {
        var sb = new System.Text.StringBuilder();
        if (!node.TryGetProperty("content", out var content))
            return string.Empty;

        foreach (var child in content.EnumerateArray())
        {
            if (child.TryGetProperty("type", out var ct)
                && ct.GetString() == "text"
                && child.TryGetProperty("text", out var txt))
                sb.Append(txt.GetString());
            else
                sb.Append(ExtractTextFromNode(child));
        }
        return sb.ToString();
    }

    private static ParsedXrayDescription ParsePlainText(string description)
    {
        // Detect section headers: **Header:** or ## Header or Header: on its own line
        var sectionPattern = new Regex(
            @"(?:^|
)(?:\*\*(?<h>[^*]+)\*\*\s*:?|##\s*(?<h>[^
]+)|(?<h>[A-Z][A-Za-z\s/\-]+)\s*:)\s*
",
            RegexOptions.Multiline);

        var lines = new List<(string? Section, string Text, bool IsBullet)>();
        var matches = sectionPattern.Matches(description);

        if (matches.Count == 0)
        {
            AppendBodyLines(description, null, lines);
        }
        else
        {
            if (matches[0].Index > 0)
                AppendBodyLines(description[..matches[0].Index], null, lines);

            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var heading = m.Groups["h"].Value.Trim();
                int bodyStart = m.Index + m.Length;
                int bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : description.Length;
                AppendBodyLines(description[bodyStart..bodyEnd], heading, lines);
            }
        }

        return SectioniseLines(lines);
    }

    private static void AppendBodyLines(
        string body,
        string? heading,
        List<(string? Section, string Text, bool IsBullet)> lines)
    {
        foreach (var rawLine in body.Split('\n'))
        {
            var trimmed = rawLine.Trim().TrimStart('-', '*').Trim();
            // Remove leading number+dot (e.g. "1. " -> "")
            trimmed = Regex.Replace(trimmed, @"^\d+\.\s*", string.Empty);
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            bool isBullet = rawLine.TrimStart().StartsWith('-')
                         || rawLine.TrimStart().StartsWith('*')
                         || Regex.IsMatch(rawLine.TrimStart(), @"^\d+\.");
            lines.Add((heading, trimmed, isBullet));
        }
    }

    private static ParsedXrayDescription SectioniseLines(
        IEnumerable<(string? Section, string Text, bool IsBullet)> lines)
    {
        var result = new ParsedXrayDescription();
        string? currentSection = null;
        var other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (section, text, _) in lines)
        {
            // A (section, empty-text) tuple is a heading marker
            if (section != null && text == string.Empty)
            {
                currentSection = section;
                continue;
            }

            if (section != null)
                currentSection = section;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (currentSection != null && IsSection(currentSection, PreconditionNames))
                result.Preconditions.Add(text);
            else if (currentSection != null && IsSection(currentSection, TestDataNames))
                result.TestData = result.TestData is null ? text : result.TestData + "\n" + text;
            else if (currentSection != null && IsSection(currentSection, OutcomeNames))
                result.ExpectedOutcomes.Add(text);
            else
            {
                var key = currentSection ?? "Description";
                if (!other.TryGetValue(key, out var buf))
                    other[key] = buf = [];
                buf.Add(text);
            }
        }

        foreach (var (k, v) in other)
            result.OtherSections[k] = string.Join("\n", v);

        return result;
    }

    private static bool IsSection(string actual, string[] names)
        => names.Any(n => actual.Equals(n, StringComparison.OrdinalIgnoreCase));
}
