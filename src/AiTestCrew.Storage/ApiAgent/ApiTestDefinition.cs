using System.Text.Json.Serialization;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.Agents.ApiAgent;

/// <summary>
/// The definition of an API test — HTTP request details and expected response.
/// Used inside <see cref="AiTestCrew.Agents.Persistence.TestObjective"/> for API targets.
///
/// REQ-007: <see cref="ApiAssertions"/> and <see cref="Captures"/> are the structured
/// validation surface. The legacy <see cref="ExpectedStatus"/>,
/// <see cref="ExpectedBodyContains"/>, and <see cref="ExpectedBodyNotContains"/> fields
/// are preserved for back-compat and automatically promoted into <see cref="ApiAssertions"/>
/// at load time via <see cref="NormaliseLegacyFields"/> (one-way shim).
/// </summary>
public class ApiTestDefinition
{
    public string Method { get; set; } = "GET";
    public string Endpoint { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
    public Dictionary<string, string> QueryParams { get; set; } = [];
    public object? Body { get; set; }

    // ── Legacy validation fields (back-compat — still written by old clients) ──
    // These drive NormaliseLegacyFields() at load time. On save, only ApiAssertions
    // is written; these are left at their defaults so the serialised JSON is clean.
    public int ExpectedStatus { get; set; } = 200;
    public List<string> ExpectedBodyContains { get; set; } = [];
    public List<string> ExpectedBodyNotContains { get; set; } = [];

    public bool IsFuzzTest { get; set; }

    // ── REQ-007: structured assertions ──────────────────────────────────────────

    /// <summary>
    /// Operator-driven assertions on the response status code, headers, and body.
    /// When non-empty, these are authoritative: the LLM validation step is
    /// skipped entirely. See <c>ApiTestAgent.ValidateResponseAsync</c> precedence rules.
    ///
    /// Automatically populated from legacy fields by <see cref="NormaliseLegacyFields"/>
    /// when those fields are non-default and <see cref="ApiAssertions"/> is empty.
    /// </summary>
    public List<ApiAssertion> ApiAssertions { get; set; } = [];

    // ── REQ-007: response captures ───────────────────────────────────────────────

    /// <summary>
    /// Captures scalar values from the HTTP response and binds them as
    /// <c>{{Token}}</c> entries in the per-objective post-step run context.
    /// Sibling post-steps (API, DB, UI, EventAssert) see captured values via
    /// token substitution.
    /// </summary>
    public List<ApiCapture> Captures { get; set; } = [];

    /// <summary>
    /// Optional post-steps (sub-actions / sub-verifications) that run AFTER this
    /// API call completes. Each post-step targets a UI surface, aseXML delivery,
    /// or DB check and receives the parent call's context (method, endpoint,
    /// response status) via <c>{{Token}}</c> substitution.
    /// </summary>
    public List<VerificationStep> PostSteps { get; set; } = [];

    // ── Legacy-shim: promote old fields → ApiAssertions ─────────────────────────

    /// <summary>
    /// One-way promotion of the legacy <see cref="ExpectedStatus"/>,
    /// <see cref="ExpectedBodyContains"/>, and <see cref="ExpectedBodyNotContains"/>
    /// fields into typed <see cref="ApiAssertions"/> entries.
    ///
    /// Called by the agent after deserialisation. Idempotent: when
    /// <see cref="ApiAssertions"/> already has entries the method is a no-op so
    /// a round-tripped (load → save → load) test set is never double-promoted.
    ///
    /// Promotion rules:
    /// <list type="bullet">
    ///   <item><see cref="ExpectedStatus"/> != 200 OR <see cref="ExpectedBodyContains"/>
    ///     / <see cref="ExpectedBodyNotContains"/> non-empty → promote.</item>
    ///   <item><see cref="ExpectedStatus"/> == 200 and both body lists empty AND
    ///     <see cref="ApiAssertions"/> empty → leave <see cref="ApiAssertions"/>
    ///     empty so the LLM validator still runs (Normal-mode behaviour).</item>
    /// </list>
    /// </summary>
    public void NormaliseLegacyFields()
    {
        // Already has structured assertions — do not overwrite or double-promote.
        if (ApiAssertions.Count > 0) return;

        var hasNonDefaultStatus = ExpectedStatus != 200;
        var hasBodyContains = ExpectedBodyContains.Count > 0;
        var hasBodyNotContains = ExpectedBodyNotContains.Count > 0;

        // Nothing to promote — leave ApiAssertions empty so the LLM validator fires.
        if (!hasNonDefaultStatus && !hasBodyContains && !hasBodyNotContains) return;

        // Promote status assertion.
        ApiAssertions.Add(new ApiAssertion
        {
            Source = ApiAssertionSource.Status,
            Operator = AssertionOperator.Equals,
            Expected = ExpectedStatus.ToString(),
        });

        // Promote body-contains assertions.
        foreach (var expr in ExpectedBodyContains)
        {
            ApiAssertions.Add(new ApiAssertion
            {
                Source = ApiAssertionSource.BodyText,
                Operator = AssertionOperator.Contains,
                Expected = expr,
                IgnoreCase = true,
            });
        }

        // Promote body-not-contains assertions.
        foreach (var expr in ExpectedBodyNotContains)
        {
            ApiAssertions.Add(new ApiAssertion
            {
                Source = ApiAssertionSource.BodyText,
                Operator = AssertionOperator.NotContains,
                Expected = expr,
                IgnoreCase = true,
            });
        }
    }

    /// <summary>
    /// Creates an <see cref="ApiTestDefinition"/> from a legacy <see cref="ApiTestCase"/>.
    /// </summary>
    public static ApiTestDefinition FromTestCase(ApiTestCase tc) => new()
    {
        Method = tc.Method,
        Endpoint = tc.Endpoint,
        Headers = tc.Headers,
        QueryParams = tc.QueryParams,
        Body = tc.Body,
        ExpectedStatus = tc.ExpectedStatus,
        ExpectedBodyContains = tc.ExpectedBodyContains,
        ExpectedBodyNotContains = tc.ExpectedBodyNotContains,
        IsFuzzTest = tc.IsFuzzTest,
        ApiAssertions = tc.ApiAssertions,
        Captures = tc.Captures,
        PostSteps = tc.PostSteps
    };

    /// <summary>
    /// Creates an <see cref="ApiTestCase"/> from this definition (for agent execution).
    /// </summary>
    public ApiTestCase ToTestCase(string name) => new()
    {
        Name = name,
        Method = Method,
        Endpoint = Endpoint,
        Headers = Headers,
        QueryParams = QueryParams,
        Body = Body,
        ExpectedStatus = ExpectedStatus,
        ExpectedBodyContains = ExpectedBodyContains,
        ExpectedBodyNotContains = ExpectedBodyNotContains,
        IsFuzzTest = IsFuzzTest,
        ApiAssertions = ApiAssertions,
        Captures = Captures,
        PostSteps = PostSteps
    };
}
