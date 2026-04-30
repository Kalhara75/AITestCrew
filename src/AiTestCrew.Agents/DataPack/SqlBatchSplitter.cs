using System.Text;
using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.DataPack;

/// <summary>
/// Splits a T-SQL script into individual batches on <c>GO</c> terminator lines,
/// the way <c>sqlcmd</c> does. <c>GO</c> is not valid T-SQL — <c>SqlCommand</c>
/// will reject a multi-batch script unless it is split client-side first.
///
/// Recognises a separator on any line matching <c>^\s*GO\s*(--.*)?$</c>
/// (case-insensitive). A <c>GO</c> token inside a <c>'...'</c> string literal,
/// a <c>--</c> line comment, or a <c>/* ... */</c> block comment is NOT treated
/// as a separator. Block comments and unterminated single-line strings carry
/// their state across line boundaries.
///
/// Limitations (out of scope for v1):
/// <list type="bullet">
///   <item><description>No support for <c>sqlcmd</c> features (<c>:r</c>, <c>GO 5</c>, <c>:setvar</c>).</description></item>
///   <item><description>Multi-line string literals containing a standalone <c>GO</c> line are very rare and will be incorrectly split.</description></item>
/// </list>
/// </summary>
public static class SqlBatchSplitter
{
    private static readonly Regex GoLineRx =
        new(@"^\s*GO\s*(--.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> Split(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return Array.Empty<string>();

        var batches = new List<string>();
        var current = new StringBuilder();
        bool inBlockComment = false;
        bool inString = false;

        var normalised = sql.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');

        foreach (var line in lines)
        {
            if (!inBlockComment && !inString && GoLineRx.IsMatch(line))
            {
                AppendBatch(batches, current);
                current.Clear();
                continue;
            }

            current.Append(line);
            current.Append('\n');
            (inBlockComment, inString) = UpdateState(line, inBlockComment, inString);
        }

        AppendBatch(batches, current);
        return batches;
    }

    private static (bool InBlockComment, bool InString) UpdateState(
        string line, bool inBlockComment, bool inString)
    {
        int i = 0;
        while (i < line.Length)
        {
            if (inBlockComment)
            {
                int close = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (close < 0) return (true, inString);
                inBlockComment = false;
                i = close + 2;
                continue;
            }

            if (inString)
            {
                if (line[i] == '\'')
                {
                    if (i + 1 < line.Length && line[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }
                    inString = false;
                    i++;
                    continue;
                }
                i++;
                continue;
            }

            int lineComment = line.IndexOf("--", i, StringComparison.Ordinal);
            int blockOpen = line.IndexOf("/*", i, StringComparison.Ordinal);
            int singleQuote = line.IndexOf('\'', i);

            int next = MinNonNegative(lineComment, blockOpen, singleQuote);
            if (next < 0) return (false, false);

            if (next == lineComment)
                return (false, false);

            if (next == blockOpen)
            {
                inBlockComment = true;
                i = next + 2;
                continue;
            }

            inString = true;
            i = next + 1;
        }
        return (inBlockComment, inString);
    }

    private static int MinNonNegative(int a, int b, int c)
    {
        int best = -1;
        if (a >= 0) best = a;
        if (b >= 0 && (best < 0 || b < best)) best = b;
        if (c >= 0 && (best < 0 || c < best)) best = c;
        return best;
    }

    private static void AppendBatch(List<string> batches, StringBuilder current)
    {
        var batch = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(batch))
            batches.Add(batch);
    }
}
