using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.Teardown;

/// <summary>
/// Static SQL safety checks for user-supplied teardown statements. All checks
/// run BEFORE any connection opens so a bad statement never reaches the DB.
///
/// Two accepted shapes:
/// <list type="number">
///   <item><description>
///     A constrained DML statement — must contain a <c>WHERE</c> token and
///     must not contain any reserved destructive keyword
///     (<c>TRUNCATE</c>, <c>DROP</c>, <c>ALTER</c>, <c>CREATE</c>,
///     <c>EXEC</c>, <c>EXECUTE</c>, <c>SHUTDOWN</c>, <c>GRANT</c>,
///     <c>REVOKE</c>, <c>MERGE</c>).
///   </description></item>
///   <item><description>
///     A stored-procedure call — <c>EXEC[UTE] [schema.]&lt;procName&gt; [params]</c>
///     where <c>&lt;procName&gt;</c> matches one of the configured allowed prefixes
///     (default <c>usp_</c>). Lets dev-installed teardown procs (shipped via the
///     data-pack runner) be invoked without dropping the rest of the guardrail.
///     The proc-call branch skips the <c>WHERE</c> requirement, but still
///     rejects any further destructive keyword in the statement and any
///     additional <c>EXEC</c> after the leading one.
///   </description></item>
/// </list>
///
/// Line (<c>-- ...</c>) and block (<c>/* ... */</c>) comments are stripped
/// before matching so an adversarial comment can't conceal or sneak in a
/// denied keyword.
/// </summary>
public static class SqlGuardrails
{
    private static readonly string[] DefaultAllowedExecPrefixes = ["usp_"];

    private static readonly string[] DeniedKeywordsExceptExec =
    [
        "TRUNCATE", "DROP", "ALTER", "CREATE",
        "SHUTDOWN", "GRANT", "REVOKE", "MERGE"
    ];

    private static readonly string[] DeniedExecKeywords = ["EXEC", "EXECUTE"];

    private static readonly Regex LineCommentRx =
        new(@"--[^\r\n]*", RegexOptions.Compiled);

    private static readonly Regex BlockCommentRx =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex WhereRx =
        new(@"\bWHERE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches "EXEC[UTE] [schema.]proc_name" at the start of the cleaned SQL.
    // Captures the unqualified proc name in the "name" group. Tolerates
    // bracket-quoted identifiers ([schema].[proc]).
    private static readonly Regex ExecCallStartRx = new(
        @"^\s*EXEC(UTE)?\s+(?:\[?[A-Za-z_][\w]*\]?\.)?\[?(?<name>[A-Za-z_][\w]*)\]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (bool Ok, string? Reason) Validate(
        string? sql,
        IReadOnlyList<string>? allowedExecPrefixes = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (false, "SQL is empty.");

        var cleaned = BlockCommentRx.Replace(sql, " ");
        cleaned = LineCommentRx.Replace(cleaned, " ");

        var prefixes = allowedExecPrefixes is { Count: > 0 }
            ? allowedExecPrefixes
            : DefaultAllowedExecPrefixes;

        var execMatch = ExecCallStartRx.Match(cleaned);
        if (execMatch.Success)
        {
            var procName = execMatch.Groups["name"].Value;

            var matchesPrefix = prefixes.Any(p =>
                procName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (!matchesPrefix)
            {
                return (false,
                    $"EXEC of '{procName}' is not allowed — proc name must start with one of: " +
                    string.Join(", ", prefixes) + ".");
            }

            // Allowed proc call. Reject if any other destructive keyword
            // appears anywhere in the statement (catches "EXEC usp_X; DROP ...").
            foreach (var kw in DeniedKeywordsExceptExec)
            {
                var rx = new Regex($@"\b{kw}\b", RegexOptions.IgnoreCase);
                if (rx.IsMatch(cleaned))
                    return (false,
                        $"SQL contains reserved keyword '{kw}' — only the leading EXEC and its parameters are allowed.");
            }

            // Reject additional EXEC after the leading one — only one proc
            // call per teardown step, no chained "EXEC usp_X; EXEC sp_..."
            var remainder = cleaned.Substring(execMatch.Length);
            foreach (var kw in DeniedExecKeywords)
            {
                var rx = new Regex($@"\b{kw}\b", RegexOptions.IgnoreCase);
                if (rx.IsMatch(remainder))
                    return (false,
                        $"SQL contains an additional '{kw}' after the leading proc call — only one EXEC per teardown step is allowed.");
            }

            return (true, null);
        }

        // Non-EXEC statement: WHERE required, every destructive keyword denied.
        if (!WhereRx.IsMatch(cleaned))
            return (false,
                "SQL must contain a WHERE clause — refusing to run an unconstrained statement.");

        foreach (var kw in DeniedKeywordsExceptExec.Concat(DeniedExecKeywords))
        {
            var rx = new Regex($@"\b{kw}\b", RegexOptions.IgnoreCase);
            if (rx.IsMatch(cleaned))
                return (false,
                    $"SQL contains reserved keyword '{kw}' — teardown is limited to DELETE/UPDATE with a WHERE clause, or EXEC of an allowed stored procedure (default prefix: usp_).");
        }

        return (true, null);
    }
}
