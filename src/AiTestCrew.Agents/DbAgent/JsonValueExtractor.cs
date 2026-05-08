using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;

namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Thin wrapper over <see cref="Json.Path.JsonPath"/> (json-everything's MIT-licensed
/// JsonPath.Net package) used by <see cref="ColumnAssertionEvaluator"/> and the DB
/// capture path. Distinguishes <em>"column is not JSON"</em> from
/// <em>"path not found"</em> from <em>"path resolved to JSON null"</em> so the
/// evaluator can produce typed failure reasons rather than generic exceptions.
/// </summary>
public static class JsonValueExtractor
{
    public enum ExtractionStatus
    {
        /// <summary>Path resolved to a non-null JSON value.</summary>
        Found,
        /// <summary>Path resolved, but to a JSON <c>null</c>. Treated like SQL NULL by IsNull / IsNotNull.</summary>
        FoundNull,
        /// <summary>Column value is not parseable JSON.</summary>
        NotJson,
        /// <summary>Path is syntactically invalid.</summary>
        InvalidPath,
        /// <summary>Path is valid and JSON parsed, but no node matches it.</summary>
        NotFound,
    }

    /// <summary>
    /// Attempts to extract <paramref name="jsonPath"/> from the JSON text in
    /// <paramref name="columnValue"/>. Returns <see cref="ExtractionStatus.Found"/>
    /// (with <paramref name="value"/> populated) on success.
    /// </summary>
    public static ExtractionStatus TryExtract(
        string? columnValue,
        string jsonPath,
        out JsonNode? value,
        [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;

        if (columnValue is null)
        {
            error = "column value is null";
            return ExtractionStatus.NotJson;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(columnValue);
        }
        catch (JsonException ex)
        {
            error = $"column value is not JSON: {ex.Message}";
            return ExtractionStatus.NotJson;
        }

        if (parsed is null)
        {
            // The text was the literal "null" — root is JSON null.
            // If the user asked for "$" they get null; any other path is a miss.
            if (jsonPath == "$")
                return ExtractionStatus.FoundNull;
            error = $"JSON path '{jsonPath}' not found in column (column value is JSON null)";
            return ExtractionStatus.NotFound;
        }

        JsonPath path;
        try
        {
            path = JsonPath.Parse(jsonPath);
        }
        catch (Exception ex)
        {
            error = $"JSON path '{jsonPath}' is invalid: {ex.Message}";
            return ExtractionStatus.InvalidPath;
        }

        var result = path.Evaluate(parsed);
        if (result.Matches.Count == 0)
        {
            error = $"JSON path '{jsonPath}' not found in column";
            return ExtractionStatus.NotFound;
        }

        var match = result.Matches[0].Value;
        if (match is null) return ExtractionStatus.FoundNull;

        value = match;
        return ExtractionStatus.Found;
    }

    /// <summary>
    /// Renders a JSON node as a scalar string. Strings come back unquoted; numbers
    /// and booleans stringify via their JSON representation; objects and arrays are
    /// emitted as compact JSON text so the operator logic can still match against
    /// them when the user's expected value happens to be JSON.
    /// </summary>
    public static string ToScalarString(JsonNode node)
    {
        return node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s ?? "",
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
            JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValue v when v.TryGetValue<decimal>(out var m) => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValue v => v.ToJsonString(),
            _ => node.ToJsonString(),
        };
    }
}
