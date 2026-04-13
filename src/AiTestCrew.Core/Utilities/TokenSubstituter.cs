using System.Text.RegularExpressions;

namespace AiTestCrew.Core.Utilities;

/// <summary>
/// Shared <c>{{Token}}</c> substitution used by:
/// <list type="bullet">
///   <item><description><c>AseXmlRenderer</c> — template-body token replacement (strict mode).</description></item>
///   <item><description>aseXML delivery agent — UI verification step fields (lenient mode).</description></item>
/// </list>
///
/// Token grammar: <c>{{FieldName}}</c> where FieldName is a C# identifier
/// (<c>[A-Za-z_][A-Za-z0-9_]*</c>). Whitespace inside the braces is allowed.
/// </summary>
public static class TokenSubstituter
{
    public static readonly Regex TokenRx =
        new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Replaces <c>{{Token}}</c> occurrences in <paramref name="input"/> with values from
    /// <paramref name="context"/>. Unknown tokens are returned as-is unless
    /// <paramref name="throwOnMissing"/> is <c>true</c>.
    ///
    /// A null input returns null; an empty input returns empty unchanged.
    /// </summary>
    public static string? Substitute(
        string? input,
        IReadOnlyDictionary<string, string> context,
        bool throwOnMissing = false,
        ICollection<string>? unknownTokens = null)
    {
        if (input is null || input.Length == 0) return input;
        if (!input.Contains("{{", StringComparison.Ordinal)) return input;  // cheap fast-path

        return TokenRx.Replace(input, m =>
        {
            var key = m.Groups[1].Value;
            if (context.TryGetValue(key, out var value)) return value ?? "";
            if (throwOnMissing)
            {
                throw new TokenSubstitutionException(
                    $"Unknown token '{{{{{key}}}}}' (no entry in context for '{key}').");
            }
            unknownTokens?.Add(key);
            return m.Value;  // leave {{Token}} literal
        });
    }

    /// <summary>
    /// Collects the set of token names referenced by <paramref name="input"/> without
    /// substituting. Useful for diagnostics and lint tooling.
    /// </summary>
    public static IReadOnlyCollection<string> ExtractTokens(string? input)
    {
        if (string.IsNullOrEmpty(input)) return Array.Empty<string>();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenRx.Matches(input!)) set.Add(m.Groups[1].Value);
        return set;
    }
}

/// <summary>Raised by strict-mode <see cref="TokenSubstituter.Substitute"/> when a token is unknown.</summary>
public class TokenSubstitutionException : Exception
{
    public TokenSubstitutionException(string message) : base(message) { }
}
