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
    /// </summary>
    public static string ToSlug(string input)
    {
        var lower = input.ToLowerInvariant();
        var hyphenated = Regex.Replace(lower, @"[^a-z0-9]+", "-");
        var collapsed = Regex.Replace(hyphenated, @"-{2,}", "-").Trim('-');
        if (collapsed.Length <= 80) return collapsed;
        var truncated = collapsed[..80];
        var lastHyphen = truncated.LastIndexOf('-');
        return lastHyphen > 0 ? truncated[..lastHyphen] : truncated;
    }
}
