using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.Teardown;

/// <summary>
/// Static SQL safety checks for user-supplied teardown statements. All checks
/// run BEFORE any connection opens so a bad statement never reaches the DB.
///
/// Rules (case-insensitive):
/// <list type="bullet">
///   <item><description>Must contain a <c>WHERE</c> token (word-boundary) — prevents accidental table-wide deletes.</description></item>
///   <item><description>Must not contain any reserved destructive keyword (<c>TRUNCATE</c>, <c>DROP</c>, <c>ALTER</c>, <c>CREATE</c>, <c>EXEC</c>, <c>EXECUTE</c>, <c>SHUTDOWN</c>, <c>GRANT</c>, <c>REVOKE</c>, <c>MERGE</c>).</description></item>
/// </list>
///
/// Line (<c>-- ...</c>) and block (<c>/* ... */</c>) comments are stripped
/// before matching so an adversarial comment can't conceal or sneak in a
/// denied keyword.
/// </summary>
public static class SqlGuardrails
{
    private static readonly string[] DeniedKeywords =
    [
        "TRUNCATE", "DROP", "ALTER", "CREATE",
        "EXEC", "EXECUTE", "SHUTDOWN",
        "GRANT", "REVOKE", "MERGE"
    ];

    private static readonly Regex LineCommentRx =
        new(@"--[^\r\n]*", RegexOptions.Compiled);

    private static readonly Regex BlockCommentRx =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex WhereRx =
        new(@"\bWHERE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (bool Ok, string? Reason) Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (false, "SQL is empty.");

        var cleaned = BlockCommentRx.Replace(sql, " ");
        cleaned = LineCommentRx.Replace(cleaned, " ");

        if (!WhereRx.IsMatch(cleaned))
            return (false, "SQL must contain a WHERE clause — refusing to run an unconstrained statement.");

        foreach (var kw in DeniedKeywords)
        {
            var rx = new Regex($@"\b{kw}\b", RegexOptions.IgnoreCase);
            if (rx.IsMatch(cleaned))
                return (false, $"SQL contains reserved keyword '{kw}' — teardown is limited to DELETE/UPDATE with a WHERE clause.");
        }

        return (true, null);
    }
}
