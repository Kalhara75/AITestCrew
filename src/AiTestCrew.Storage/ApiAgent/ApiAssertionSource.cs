using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.ApiAgent;

/// <summary>
/// Identifies where in an HTTP response an <see cref="ApiAssertion"/> or
/// <see cref="ApiCapture"/> extracts its value from.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiAssertionSource
{
    /// <summary>The HTTP response status code (integer).</summary>
    Status,

    /// <summary>A named response header value. Requires <see cref="ApiAssertion.HeaderName"/>.</summary>
    Header,

    /// <summary>A JSONPath-extracted scalar from the parsed JSON response body. Requires <see cref="ApiAssertion.JsonPath"/>.</summary>
    Body,

    /// <summary>The raw response body as a single string (pre-parse, suitable for substring / regex operators).</summary>
    BodyText,
}
