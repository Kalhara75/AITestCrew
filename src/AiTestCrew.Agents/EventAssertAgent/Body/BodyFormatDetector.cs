using AiTestCrew.Agents.EventAssertAgent;

namespace AiTestCrew.Agents.EventAssertAgent.Body;

/// <summary>
/// Resolves an effective <see cref="BodyFormat"/> for a received message,
/// honouring an explicit configured value and falling back to <see cref="BodyFormat.Auto"/>
/// sniffing (ContentType first, then first non-whitespace byte). Pure;
/// shared between the agent's evaluation loop and the WebApi peek endpoint.
/// </summary>
public static class BodyFormatDetector
{
    /// <summary>
    /// Returns the format the agent should use for body extraction. When
    /// <paramref name="configured"/> is anything other than <see cref="BodyFormat.Auto"/>
    /// it is honoured verbatim (the user knows their producer better than we do).
    /// </summary>
    public static BodyFormat Resolve(BodyFormat configured, string? contentType, ReadOnlySpan<byte> body)
    {
        if (configured != BodyFormat.Auto) return configured;

        // 1. ContentType sniff — case-insensitive contains check on common
        //    substrings ("json" / "xml") so mislabelled mixed-case content types
        //    (e.g. "application/Json", "application/json; charset=utf-8") still resolve.
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return BodyFormat.Json;
            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                return BodyFormat.Xml;
            if (contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
                return BodyFormat.Binary;
            // text/plain et al. fall through to byte sniff for redundancy.
        }

        // 2. First-non-whitespace-byte sniff. Conservative — only commit to
        //    JSON/XML when the leading character is unambiguous; otherwise Text.
        for (var i = 0; i < body.Length; i++)
        {
            var c = (char)body[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
            return c switch
            {
                '{' or '[' => BodyFormat.Json,
                '<' => BodyFormat.Xml,
                _ => BodyFormat.Text,
            };
        }

        // Empty body: nothing to dispatch on. Treat as Text — Body.* / BodyXml.*
        // paths will fail with a typed reason at extraction time.
        return BodyFormat.Text;
    }
}
