using System.Text;
using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.Agents.EventAssertAgent.Body;

/// <summary>
/// Adapts REQ-002's <see cref="JsonValueExtractor"/> (JsonPath.Net wrapper)
/// to <c>Body.&lt;jsonpath&gt;</c> field paths on a Service Bus message body.
/// The body is treated as UTF-8 text; if it isn't valid UTF-8 / valid JSON,
/// extraction returns a typed reason rather than throwing.
/// </summary>
public static class JsonBodyExtractor
{
    /// <summary>
    /// Extracts the JSONPath suffix (everything after <c>"Body."</c>) from a
    /// UTF-8 body. <paramref name="jsonPath"/> is the BARE path (e.g.
    /// <c>"$.Order.Id"</c> or <c>"Order.Id"</c> — the leading <c>$.</c> is
    /// optional; the wrapper normalises both forms).
    /// </summary>
    public static ExtractResult Extract(byte[] body, string jsonPath)
    {
        if (body.Length == 0)
            return ExtractResult.Failed($"body is empty — JSON path '{jsonPath}' cannot be evaluated");

        string text;
        try
        {
            text = Encoding.UTF8.GetString(body);
        }
        catch (Exception ex)
        {
            return ExtractResult.Failed($"body is not valid UTF-8: {ex.Message}");
        }

        // Normalise "Order.Id" → "$.Order.Id" so users don't have to know the
        // JsonPath leading-$ convention.
        var normalised = jsonPath.StartsWith("$") ? jsonPath : "$." + jsonPath.TrimStart('.');

        var status = JsonValueExtractor.TryExtract(text, normalised, out var node, out var err);
        return status switch
        {
            JsonValueExtractor.ExtractionStatus.Found =>
                ExtractResult.FoundValue(JsonValueExtractor.ToScalarString(node!)),
            JsonValueExtractor.ExtractionStatus.FoundNull =>
                ExtractResult.FoundNullValue(),
            JsonValueExtractor.ExtractionStatus.NotJson =>
                ExtractResult.Failed($"body is not JSON ({err})"),
            JsonValueExtractor.ExtractionStatus.InvalidPath =>
                ExtractResult.Failed($"JSON path '{normalised}' is invalid: {err}"),
            _ =>
                ExtractResult.Failed($"JSON path '{normalised}' not found in body"),
        };
    }
}
