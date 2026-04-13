using System.Security.Cryptography;

namespace AiTestCrew.Agents.AseXmlAgent.Templates;

/// <summary>
/// Deterministic-but-unique value generators for aseXML auto fields.
/// Adding a new generator = adding a method here and referencing it by name
/// from the manifest's <see cref="FieldSpec.Generator"/>.
/// </summary>
public static class FieldGenerators
{
    private const string Rand8Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>
    /// ISO-8601 local timestamp with a fixed offset (e.g. "2025-05-05T18:01:24+10:00").
    /// Uses the machine's current UTC, then applies the configured offset.
    /// </summary>
    public static string NowOffset(string? offset)
    {
        var off = ParseOffset(offset);
        var now = DateTimeOffset.UtcNow.ToOffset(off);
        return now.ToString("yyyy-MM-ddTHH:mm:sszzz");
    }

    /// <summary>
    /// Date-only equivalent of <see cref="NowOffset"/>, e.g. "2025-05-05".
    /// </summary>
    public static string Today(string? offset)
    {
        var off = ParseOffset(offset);
        var now = DateTimeOffset.UtcNow.ToOffset(off);
        return now.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Substitutes "{rand8}" in the pattern with a fresh 8-char uppercase alphanumeric.
    /// If pattern is null or empty, returns just the random token.
    /// </summary>
    public static string MessageId(string? pattern) => SubstituteRand8(pattern);

    /// <summary>Same as <see cref="MessageId"/> — separate method kept for manifest clarity.</summary>
    public static string TransactionId(string? pattern) => SubstituteRand8(pattern);

    /// <summary>
    /// Dispatches to the right generator for the given spec. Returns null if the
    /// generator name is unknown (caller decides whether that's an error).
    /// </summary>
    public static string? Generate(FieldSpec spec) => spec.Generator?.ToLowerInvariant() switch
    {
        "messageid" => MessageId(spec.Pattern),
        "transactionid" => TransactionId(spec.Pattern),
        "nowoffset" => NowOffset(spec.Offset),
        "today" => Today(spec.Offset),
        _ => null
    };

    private static string SubstituteRand8(string? pattern)
    {
        var token = Rand8();
        if (string.IsNullOrEmpty(pattern)) return token;
        return pattern.Replace("{rand8}", token, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rand8()
    {
        Span<char> buf = stackalloc char[8];
        for (var i = 0; i < buf.Length; i++)
            buf[i] = Rand8Chars[RandomNumberGenerator.GetInt32(Rand8Chars.Length)];
        return new string(buf);
    }

    private static TimeSpan ParseOffset(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset)) return TimeSpan.Zero;
        // Accept formats like "+10:00", "-05:30", "+1000"
        var s = offset.Trim();
        if (TimeSpan.TryParse(s.TrimStart('+'), out var positive))
            return s.StartsWith('-') ? -positive : positive;
        if (s.Length == 5 && (s[0] == '+' || s[0] == '-')
            && int.TryParse(s.AsSpan(1, 2), out var hh)
            && int.TryParse(s.AsSpan(3, 2), out var mm))
        {
            var ts = new TimeSpan(hh, mm, 0);
            return s[0] == '-' ? -ts : ts;
        }
        return TimeSpan.Zero;
    }
}
