using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.Base;

/// <summary>
/// Shared utilities for parsing LLM JSON responses.
/// Used by both agents and WebApi endpoints.
/// </summary>
public static class LlmJsonHelper
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Strips markdown fences, leading/trailing text, and extracts
    /// the JSON array or object from an LLM response.
    /// </summary>
    public static string CleanJsonResponse(string raw)
    {
        var cleaned = Regex.Replace(raw, @"```(?:json)?\s*", "");
        cleaned = cleaned.Replace("```", "").Trim();

        var firstBracket = cleaned.IndexOfAny(['{', '[']);
        var lastBracket = cleaned.LastIndexOfAny(['}', ']']);

        if (firstBracket >= 0 && lastBracket > firstBracket)
            cleaned = cleaned[firstBracket..(lastBracket + 1)];

        return cleaned;
    }

    /// <summary>
    /// Cleans an LLM response and deserializes it as the specified type.
    /// Returns null on parse failure.
    /// </summary>
    public static T? DeserializeLlmResponse<T>(string raw)
    {
        var cleaned = CleanJsonResponse(raw);
        try
        {
            return JsonSerializer.Deserialize<T>(cleaned, JsonOpts);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
