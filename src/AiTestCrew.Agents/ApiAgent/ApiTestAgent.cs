using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.Base;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.ApiAgent;

/// <summary>
/// Tests REST API endpoints using LLM-generated test cases.
/// 
/// Flow:
/// 1. Receives a test task (e.g. "Test the /api/products endpoint")
/// 2. Optionally loads the OpenAPI spec for context
/// 3. Asks the LLM to generate test cases (happy path, edge cases, errors)
/// 4. Executes each test case via HttpClient
/// 5. Asks the LLM to validate each response
/// 6. Returns aggregated results
/// </summary>
public class ApiTestAgent : BaseTestAgent
{
    private readonly HttpClient _http;
    private readonly TestEnvironmentConfig _config;

    public override string Name => "API Agent";
    public override string Role => "Senior REST API Test Engineer";

    public ApiTestAgent(
        Kernel kernel,
        ILogger<ApiTestAgent> logger,
        HttpClient httpClient,
        TestEnvironmentConfig config) : base(kernel, logger)
    {
        _http = httpClient;
        _config = config;
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target is TestTargetType.API_REST
                                   or TestTargetType.API_GraphQL);

    public override async Task<TestResult> ExecuteAsync(
        TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();

        Logger.LogInformation("[{Agent}] Starting task: {Desc}", Name, task.Description);

        try
        {
            // ── Check for pre-loaded test cases (reuse mode — skip LLM generation) ──
            List<ApiTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
                && preloaded is List<ApiTestCase> saved)
            {
                testCases = saved;
                steps.Add(TestStep.Pass("load-cases",
                    $"Loaded {testCases.Count} saved test cases (reuse mode — skipping LLM generation)"));
                Logger.LogInformation("[{Agent}] Reuse mode: using {Count} saved test cases, skipping LLM generation",
                    Name, testCases.Count);
            }

            if (testCases is null)
            {
                // ── 1. Load OpenAPI spec if available ──
                string? apiSpec = null;
                if (!string.IsNullOrEmpty(_config.OpenApiSpecUrl))
                {
                    apiSpec = await TryLoadOpenApiSpecAsync(ct);
                    steps.Add(apiSpec is not null
                        ? TestStep.Pass("load-spec", "Loaded OpenAPI specification")
                        : TestStep.Pass("load-spec", "No OpenAPI spec available, using LLM inference"));
                }

                // ── 2. Discovery call — hit the primary endpoint once to capture the
                //       real response shape so Claude can generate accurate field assertions ──
                var discovery = await DiscoverEndpointAsync(task, ct);
                if (discovery is not null)
                {
                    steps.Add(TestStep.Pass("discovery",
                        $"Discovery call: {discovery.StatusCode} — captured {discovery.BodySample.Length} chars, " +
                        $"fields: {discovery.TopLevelFields}"));
                    Logger.LogInformation("[{Agent}] Discovery: {Status}, fields detected: {Fields}",
                        Name, discovery.StatusCode, discovery.TopLevelFields);
                }

                // ── 3. Ask LLM to generate test cases ──
                testCases = await GenerateTestCasesAsync(task, apiSpec, discovery, ct);
                if (testCases is null || testCases.Count == 0)
                {
                    return new TestResult
                    {
                        TaskId = task.Id,
                        AgentName = Name,
                        Status = TestStatus.Error,
                        Summary = "LLM failed to generate test cases",
                        Steps = steps,
                        Duration = sw.Elapsed
                    };
                }

                steps.Add(TestStep.Pass("generate-cases",
                    $"Generated {testCases.Count} test cases"));
                Logger.LogInformation("[{Agent}] Generated {Count} test cases", Name, testCases.Count);
            }

            // ── 4. Execute each test case ──
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                var stepResult = await ExecuteTestCaseAsync(tc, ct);
                steps.Add(stepResult);

                Logger.LogInformation("[{Agent}] {Status}: {Method} {Endpoint} - {Summary}",
                    Name, stepResult.Status, tc.Method, tc.Endpoint, stepResult.Summary);
            }

            // ── 5. Determine overall status ──
            var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
            var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
            var status = hasErrors ? TestStatus.Error
                       : hasFails ? TestStatus.Failed
                       : TestStatus.Passed;

            // ── 6. Get LLM summary ──
            var summary = await SummariseResultsAsync(steps, ct);

            return new TestResult
            {
                TaskId = task.Id,
                AgentName = Name,
                Status = status,
                Summary = summary,
                Steps = steps,
                Duration = sw.Elapsed,
                Metadata = new Dictionary<string, object>
                {
                    ["totalCases"] = testCases.Count,
                    ["baseUrl"] = _config.ApiBaseUrl,
                    ["generatedTestCases"] = testCases
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new TestResult
            {
                TaskId = task.Id,
                AgentName = Name,
                Status = TestStatus.Error,
                Summary = "Test execution was cancelled",
                Steps = steps,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] Unhandled error", Name);
            steps.Add(TestStep.Err("fatal", $"Unhandled exception: {ex.Message}"));

            return new TestResult
            {
                TaskId = task.Id,
                AgentName = Name,
                Status = TestStatus.Error,
                Summary = $"Agent error: {ex.Message}",
                Steps = steps,
                Duration = sw.Elapsed
            };
        }
    }

    // ─────────────────────────────────────────────────────
    // Core methods
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Ask the LLM to generate test cases from the task description,
    /// optionally an OpenAPI spec, and a real discovery response sample.
    /// </summary>
    private async Task<List<ApiTestCase>?> GenerateTestCasesAsync(
        TestTask task, string? apiSpec, EndpointDiscovery? discovery, CancellationToken ct)
    {
        var specSection = apiSpec is not null
            ? $"""

              Here is the OpenAPI/Swagger specification for context:
              {apiSpec[..Math.Min(apiSpec.Length, 6000)]}
              """
            : """

              No OpenAPI spec is available. Infer reasonable endpoints,
              methods, and payloads from the task description.
              """;

        var authNote = string.IsNullOrWhiteSpace(_config.AuthToken)
            ? "No authentication is configured — include auth-failure tests (401/403) where relevant."
            : $"Authentication is pre-configured ({_config.AuthScheme} token will be injected automatically). " +
              "Do NOT add an Authorization header in the test cases. " +
              "Focus on functional, boundary, and error tests rather than auth tests.";

        // Include the real response sample so Claude uses actual field names
        var discoverySection = discovery is not null
            ? $"""

              IMPORTANT — Real response from a discovery call to this endpoint:
              Status: {discovery.StatusCode}
              Response Headers: {discovery.Headers}
              Response body sample (first 1500 chars):
              {discovery.BodySample}

              Use the ACTUAL field names seen above in expectedBodyContains assertions.
              Do NOT invent field names that are not visible in the sample above.
              """
            : string.Empty;

        var prompt = $$"""
            Generate API test cases for this objective:
            "{{task.Description}}"

            Base URL: {{_config.ApiBaseUrl}}
            {{specSection}}
            {{discoverySection}}

            Authentication note: {{authNote}}

            Generate a mix of:
            - Happy path tests (valid inputs, expected success)
            - Boundary tests (empty strings, zero values, max lengths)
            - Error tests (missing required fields, invalid types, wrong data)

            RULES for expectedBodyContains:
            - Only include field names that actually appear in the discovery response above.
            - If no discovery sample is provided, leave expectedBodyContains as an empty array [].
            - Never guess or invent field names.

            For each test case, respond with this exact JSON structure:
            [
              {
                "name": "descriptive test name",
                "method": "GET|POST|PUT|DELETE|PATCH",
                "endpoint": "/api/...",
                "headers": { "Content-Type": "application/json" },
                "queryParams": {},
                "body": null,
                "expectedStatus": 200,
                "expectedBodyContains": [],
                "expectedBodyNotContains": [],
                "isFuzzTest": false
              }
            ]

            Generate 5-8 test cases. Respond ONLY with the JSON array.
            """;

        return await AskLlmForJsonAsync<List<ApiTestCase>>(prompt, ct);
    }

    /// <summary>
    /// Execute a single API test case and return a TestStep with the result.
    /// </summary>
    private async Task<TestStep> ExecuteTestCaseAsync(
        ApiTestCase tc, CancellationToken ct)
    {
        var stepSw = Stopwatch.StartNew();

        try
        {
            // Build the request
            var url = BuildUrl(tc);
            var request = new HttpRequestMessage(
                new HttpMethod(tc.Method.ToUpperInvariant()), url);

            // Add headers from the generated test case
            foreach (var (key, value) in tc.Headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }

            // Inject auth credentials from config (overrides anything the LLM generated
            // for the same header, ensuring real credentials are always used).
            InjectAuth(request);

            // Add body for non-GET requests
            if (tc.Body is not null && tc.Method.ToUpperInvariant() != "GET")
            {
                var json = tc.Body is string s ? s : JsonSerializer.Serialize(tc.Body);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            // Send it
            Logger.LogDebug("[{Agent}] >> {Method} {Url}", Name, tc.Method, url);
            var response = await _http.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            Logger.LogDebug("[{Agent}] << {Status} ({Length} bytes)",
                Name, (int)response.StatusCode, responseBody.Length);

            // Validate the response
            var validation = await ValidateResponseAsync(tc, response, responseBody, ct);

            return new TestStep
            {
                Action = $"{tc.Method} {tc.Endpoint}",
                Summary = $"[{tc.Name}] {validation.Reason}",
                Status = validation.Passed ? TestStatus.Passed : TestStatus.Failed,
                Detail = FormatResponseDetail(response, responseBody),
                Duration = stepSw.Elapsed
            };
        }
        catch (HttpRequestException ex)
        {
            return TestStep.Err(
                $"{tc.Method} {tc.Endpoint}",
                $"[{tc.Name}] Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return TestStep.Err(
                $"{tc.Method} {tc.Endpoint}",
                $"[{tc.Name}] Request timed out");
        }
    }

    /// <summary>
    /// Use a combination of rule-based checks and LLM reasoning
    /// to validate an API response.
    /// </summary>
    private async Task<ApiValidation> ValidateResponseAsync(
        ApiTestCase tc, HttpResponseMessage response, string body,
        CancellationToken ct)
    {
        var issues = new List<string>();
        var statusCode = (int)response.StatusCode;

        // ── Rule-based checks (fast, no LLM cost) ──

        // Status code check
        if (statusCode != tc.ExpectedStatus)
        {
            issues.Add($"Expected status {tc.ExpectedStatus}, got {statusCode}");
        }

        // Body contains checks
        foreach (var expected in tc.ExpectedBodyContains)
        {
            if (!body.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Response body missing expected content: '{expected}'");
            }
        }

        // Body not-contains checks
        foreach (var unexpected in tc.ExpectedBodyNotContains)
        {
            if (body.Contains(unexpected, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Response body contains unexpected content: '{unexpected}'");
            }
        }

        // If rule checks found issues, we can return early (saves LLM tokens)
        if (issues.Count > 0)
        {
            return new ApiValidation
            {
                Passed = false,
                Reason = string.Join("; ", issues),
                Issues = issues
            };
        }

        // ── LLM-based deeper validation (for complex cases) ──
        var truncatedBody = body.Length > 2000 ? body[..2000] + "...[truncated]" : body;

        // Collect advisory security header notes (informational, not hard-fail)
        var securityNotes = new List<string>();
        if (!response.Headers.Contains("X-Content-Type-Options"))
            securityNotes.Add("Missing X-Content-Type-Options header");
        if (!response.Headers.Contains("X-Frame-Options"))
            securityNotes.Add("Missing X-Frame-Options header");
        if (!response.Headers.Contains("Strict-Transport-Security"))
            securityNotes.Add("Missing Strict-Transport-Security (HSTS) header");
        var securityNote = securityNotes.Count > 0
            ? $"Advisory security observations (do NOT use these to fail the test): {string.Join(", ", securityNotes)}"
            : "All checked security headers are present.";

        var prompt = $$"""
            Validate this API response:

            Request: {{tc.Method}} {{tc.Endpoint}}
            Expected Status: {{tc.ExpectedStatus}}
            Actual Status: {{statusCode}}

            Response Body:
            {{truncatedBody}}

            Security header notes (advisory only — do not fail based on these):
            {{securityNote}}

            Check for:
            1. Is the response well-formed JSON (if expected)?
            2. Does it contain reasonable data (not empty arrays when data expected)?
            3. Are field types correct (strings, numbers, booleans)?
            4. Any error messages or stack traces in the response?
            5. Any sensitive data exposure (passwords, tokens, internal paths)?

            Base the passed/failed verdict ONLY on the response status code and body content.
            Mention security observations in the reason text if relevant, but do not fail for them.

            Respond with JSON:
            { "passed": true/false, "reason": "1-2 sentence explanation", "issues": [] }
            """;

        var llmResult = await AskLlmForJsonAsync<ApiValidation>(prompt, ct);
        return llmResult ?? new ApiValidation
        {
            Passed = true,
            Reason = "Rule checks passed, LLM validation unavailable"
        };
    }

    // ─────────────────────────────────────────────────────
    // Utility methods
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Makes a single GET call to the primary endpoint from the task description
    /// to capture the real response shape (status, headers, body sample, field names).
    /// This is passed to the LLM so it generates assertions using real field names.
    /// </summary>
    private async Task<EndpointDiscovery?> DiscoverEndpointAsync(TestTask task, CancellationToken ct)
    {
        // Extract the endpoint path from the task description — look for anything starting with /
        var match = System.Text.RegularExpressions.Regex.Match(
            task.Description, @"(/[\w/\-]+)");
        var path = match.Success ? match.Value : string.Empty;
        if (string.IsNullOrEmpty(path)) return null;

        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{path}";

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            InjectAuth(request);

            Logger.LogDebug("[{Agent}] Discovery GET {Url}", Name, url);
            var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // Extract top-level JSON field names to help Claude write accurate assertions
            var topLevelFields = string.Empty;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    body.Length > 0 ? body : "{}");
                var root = doc.RootElement;
                topLevelFields = root.ValueKind == System.Text.Json.JsonValueKind.Array
                    && root.GetArrayLength() > 0
                        ? string.Join(", ", root[0].EnumerateObject().Select(p => p.Name))
                        : root.ValueKind == System.Text.Json.JsonValueKind.Object
                            ? string.Join(", ", root.EnumerateObject().Select(p => p.Name))
                            : "(non-JSON or empty)";
            }
            catch { topLevelFields = "(could not parse JSON)"; }

            // Collect relevant response headers as a summary string
            var headers = string.Join(", ", response.Headers
                .Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));

            return new EndpointDiscovery(
                StatusCode: (int)response.StatusCode,
                Headers: headers,
                BodySample: body.Length > 1500 ? body[..1500] + "…[truncated]" : body,
                TopLevelFields: topLevelFields
            );
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[{Agent}] Discovery call failed for {Url}: {Err}", Name, url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Injects the configured auth credential into the request.
    /// Called after the LLM-generated headers are applied so real credentials
    /// always take precedence.
    /// </summary>
    private void InjectAuth(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_config.AuthToken)) return;

        // Remove any auth header the LLM may have guessed so we don't duplicate it
        request.Headers.Remove(_config.AuthHeaderName);

        var headerValue = _config.AuthScheme.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? _config.AuthToken                            // raw value  e.g. X-Api-Key: abc
            : $"{_config.AuthScheme} {_config.AuthToken}"; // scheme + token  e.g. Bearer eyJ…

        request.Headers.TryAddWithoutValidation(_config.AuthHeaderName, headerValue);
    }

    private string BuildUrl(ApiTestCase tc)
    {
        var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
        var endpoint = tc.Endpoint.StartsWith('/') ? tc.Endpoint : $"/{tc.Endpoint}";
        var url = $"{baseUrl}{endpoint}";

        if (tc.QueryParams.Count > 0)
        {
            var qs = string.Join("&", tc.QueryParams
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{qs}";
        }

        return url;
    }

    private async Task<string?> TryLoadOpenApiSpecAsync(CancellationToken ct)
    {
        try
        {
            var spec = await _http.GetStringAsync(_config.OpenApiSpecUrl, ct);
            Logger.LogInformation("[{Agent}] Loaded OpenAPI spec ({Length} chars)",
                Name, spec.Length);
            return spec;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[{Agent}] Could not load OpenAPI spec: {Err}",
                Name, ex.Message);
            return null;
        }
    }

    private static string FormatResponseDetail(
        HttpResponseMessage response, string body)
    {
        var headers = string.Join("\n",
            response.Headers.Select(h => $"  {h.Key}: {string.Join(", ", h.Value)}"));
        var truncated = body.Length > 500 ? body[..500] + "..." : body;

        return $"""
            Status: {(int)response.StatusCode} {response.ReasonPhrase}
            Headers:
            {headers}
            Body:
            {truncated}
            """;
    }
}

// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Result of the pre-test discovery call — real endpoint response
/// used to give Claude accurate field names for assertions.
/// </summary>
internal sealed record EndpointDiscovery(
    int StatusCode,
    string Headers,
    string BodySample,
    string TopLevelFields);