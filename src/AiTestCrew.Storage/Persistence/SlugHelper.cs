using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Shared slugification logic used by both TestSetRepository and ModuleRepository.
/// </summary>
public static class SlugHelper
{
    /// <summary>
    /// Converts a human-readable string into a deterministic file-safe slug.
    /// e.g. "Standing Data Replication (SDR)" → "standing-data-replication-sdr"
    /// Long inputs are truncated with a hash suffix to prevent collisions.
    /// </summary>
    public static string ToSlug(string input)
    {
        var lower = input.ToLowerInvariant();
        var hyphenated = Regex.Replace(lower, @"[^a-z0-9]+", "-");
        var collapsed = Regex.Replace(hyphenated, @"-{2,}", "-").Trim('-');
        if (collapsed.Length <= 80) return collapsed;

        // Append an 8-char hash so different long inputs produce distinct slugs
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..8].ToLowerInvariant();
        var truncated = collapsed[..70];
        var lastHyphen = truncated.LastIndexOf('-');
        var prefix = lastHyphen > 0 ? truncated[..lastHyphen] : truncated;
        return $"{prefix}-{hash}";
    }
}
