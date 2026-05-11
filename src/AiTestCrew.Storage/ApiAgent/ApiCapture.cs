namespace AiTestCrew.Agents.ApiAgent;

/// <summary>
/// Captures a scalar value from the HTTP response into the per-objective
/// post-step run context as <c>{{<see cref="As"/>}}</c>. Sibling post-steps
/// that follow this API call — inline OR deferred — see the captured value
/// via <c>StepParameterSubstituter.Apply</c>.
///
/// Captures run even when some <see cref="ApiAssertion"/> entries fail so that
/// diagnostics can inspect captured values from a failing response. If the
/// capture itself fails (path missing, <see cref="Required"/> is true), the
/// step fails independently of assertion outcomes — both are reported.
/// </summary>
public class ApiCapture
{
    /// <summary>Where in the response to extract the value from.</summary>
    public ApiAssertionSource Source { get; set; } = ApiAssertionSource.Body;

    /// <summary>
    /// Response header name — required when <see cref="Source"/> is
    /// <see cref="ApiAssertionSource.Header"/>. Compared case-insensitively.
    /// <c>{{Token}}</c>-substituted at runtime.
    /// </summary>
    public string? HeaderName { get; set; }

    /// <summary>
    /// JSONPath expression applied to the parsed JSON response body — required
    /// when <see cref="Source"/> is <see cref="ApiAssertionSource.Body"/>.
    /// <c>{{Token}}</c>-substituted at runtime (allows dynamic path segments).
    /// </summary>
    public string? JsonPath { get; set; }

    /// <summary>
    /// Token name to bind in the post-step run context, e.g. <c>"AccountId"</c>
    /// (no braces). Sibling post-steps reference it as <c>{{AccountId}}</c>.
    ///
    /// <strong>Not</strong> <c>{{Token}}</c>-substituted — substituting it would
    /// let parent context redirect captures unexpectedly.
    /// </summary>
    public string As { get; set; } = "";

    /// <summary>
    /// When true (default), the step fails if the source value is missing (e.g.
    /// header absent, JSONPath not found). When false, the token is left undefined
    /// and subsequent substitutions emit a literal <c>{{<see cref="As"/>}}</c>,
    /// logged as WARN via the existing <c>unknownTokens</c> collector.
    /// </summary>
    public bool Required { get; set; } = true;
}
