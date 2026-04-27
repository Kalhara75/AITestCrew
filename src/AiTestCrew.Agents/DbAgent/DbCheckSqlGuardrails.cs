using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Static SQL safety checks for post-step DB checks. Only read-only SELECT
/// statements are allowed — everything else is rejected before any connection
/// opens.
///
/// Rules (case-insensitive):
/// <list type="bullet">
///   <item><description>Must start with <c>SELECT</c> (after stripping comments + leading whitespace).</description></item>
///   <item><description>Must NOT contain any write/DDL/control-flow keyword
///     (<c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>MERGE</c>, <c>TRUNCATE</c>,
///     <c>DROP</c>, <c>ALTER</c>, <c>CREATE</c>, <c>EXEC</c>, <c>EXECUTE</c>,
///     <c>SHUTDOWN</c>, <c>GRANT</c>, <c>REVOKE</c>, <c>INTO</c>, <c>;</c>).</description></item>
/// </list>
///
/// The semicolon ban prevents multi-statement injection via a chained write;
/// single-statement SELECTs are the whole surface area.
/// </summary>
public static class DbCheckSqlGuardrails
{
    private static readonly string[] DeniedKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE",
        "TRUNCATE", "DROP", "ALTER", "CREATE",
        "EXEC", "EXECUTE", "SHUTDOWN",
        "GRANT", "REVOKE",
        "INTO"  // blocks SELECT INTO — would create a new table
    ];

    private static readonly Regex LineCommentRx =
        new(@"--[^\r\n]*", RegexOptions.Compiled);

    private static readonly Regex BlockCommentRx =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    public static (bool Ok, string? Reason) Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (false, "SQL is empty.");

        var cleaned = BlockCommentRx.Replace(sql, " ");
        cleaned = LineCommentRx.Replace(cleaned, " ").Trim();

        // Semicolon check first — stops chained-statement injection even if the
        // first statement is an innocent SELECT.
        if (cleaned.Contains(';'))
            return (false, "Multi-statement SQL is not allowed in DB checks (no ';' permitted).");

        if (!cleaned.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && !cleaned.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            return (false, "DB checks must be read-only — only SELECT (or a CTE starting with WITH) is allowed.");

        foreach (var kw in DeniedKeywords)
        {
            var rx = new Regex($@"\b{kw}\b", RegexOptions.IgnoreCase);
            if (rx.IsMatch(cleaned))
                return (false, $"SQL contains reserved keyword '{kw}' — DB checks are limited to read-only SELECT.");
        }

        return (true, null);
    }
}
