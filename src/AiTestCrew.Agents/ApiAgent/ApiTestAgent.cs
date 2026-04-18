using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
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
    private readonly IApiTargetResolver _resolver;

    public override string Name => "API Agent";
    public override string Role => "Senior REST API Test Engineer";

    public ApiTestAgent(
        Kernel kernel,
        ILogger<ApiTestAgent> logger,
        HttpClient httpClient,
        TestEnvironmentConfig config,
        IApiTargetResolver resolver) : base(kernel, logger)
    {
        _http = httpClient;
        _config = config;
        _resolver = resolver;
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target is TestTargetType.API_REST
                                   or TestTargetType.API_GraphQL);

    public override async Task<TestResult> ExecuteAsync(
        TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();

        // ── Resolve API target (stack + module + env) from task parameters ──
        task.Parameters.TryGetValue("ApiStackKey", out var sk);
        task.Parameters.TryGetValue("ApiModule", out var mk);
        task.Parameters.TryGetValue("EnvironmentKey", out var ek);
        var stackKey = sk as string;
        var moduleKey = mk as string;
        var envKey = ek as string;
        var resolvedBaseUrl = _resolver.ResolveApiBaseUrl(stackKey, moduleKey, envKey);
        var envParams = StepParameterSubstituter.ReadEnvironmentParameters(task.Parameters);

        Logger.LogInformation("[{Agent}] Starting task: {Desc} (target: {BaseUrl}, env: {Env})",
            Name, task.Description, resolvedBaseUrl, envKey ?? "default");

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

                // ── 2. Discovery call ──
                var discovery = await DiscoverEndpointAsync(task, resolvedBaseUrl, stackKey, envKey, ct);
                if (discovery is not null)
                {
                    steps.Add(TestStep.Pass("discovery",
                        $"Discovery call: {discovery.StatusCode} — captured {discovery.BodySample.Length} chars, " +
                        $"fields: {discovery.TopLevelFields}"));
                    Logger.LogInformation("[{Agent}] Discovery: {Status}, fields detected: {Fields}",
                        Name, discovery.StatusCode, discovery.TopLevelFields);
                }

                // ── 3. Ask LLM to generate test cases ──
                testCases = await GenerateTestCasesAsync(task, apiSpec, discovery, resolvedBaseUrl, ct);
                if (testCases is null || testCases.Count == 0)
                {
                    return new TestResult
                    {
                        ObjectiveId = task.Id,
                        ObjectiveName = task.Description,
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

            // ── 4. Execute each test case (each becomes a step) ──
            foreach (var rawTc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                // Apply per-environment {{Token}} substitution before executing.
                var tc = envParams.Count > 0
                    ? StepParameterSubstituter.Apply(rawTc, envParams)
                    : rawTc;
                var stepResult = await ExecuteTestCaseAsync(tc, resolvedBaseUrl, stackKey, envKey, ct);
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

            var metadata = new Dictionary<string, object>
            {
                ["totalCases"] = testCases.Count,
                ["baseUrl"] = resolvedBaseUrl,
                ["generatedTestCases"] = testCases
            };
            if (stackKey is not null) metadata["apiStack"] = stackKey;
            if (moduleKey is not null) metadata["apiModule"] = moduleKey;

            return new TestResult
            {
                ObjectiveId = task.Id,
                ObjectiveName = task.Description,
                AgentName = Name,
                Status = status,
                Summary = summary,
                Steps = steps,
                Duration = sw.Elapsed,
                Metadata = metadata
            };
        }
        catch (OperationCanceledException)
        {
            return new TestResult
            {
                ObjectiveId = task.Id,
                ObjectiveName = task.Description,
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
                ObjectiveId = task.Id,
                ObjectiveName = task.Description,
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
        TestTask task, string? apiSpec, EndpointDiscovery? discovery,
        string apiBaseUrl, CancellationToken ct)
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

            Base URL: {{apiBaseUrl}}
            {{specSection}}
            {{discoverySection}}

            Authentication note: {{authNote}}

            OBJECTIVE INTERPRETATION RULES:
            - Read the objective LITERALLY. Generate ONLY the test cases the objective
              explicitly asks for — nothing more.
            - If the objective specifies exact parameters, a specific endpoint, and a
              specific validation (e.g. "call X with params Y and validate Z equals V"),
              generate exactly ONE test case that does precisely that.
            - If the objective uses words like "comprehensive", "thorough", "edge cases",
              "boundary tests", "all scenarios", or "include error handling", then generate
              multiple test cases including boundary and error variations (up to 8).
            - If the objective is vague or open-ended (e.g. "test the login API" with no
              specific parameters or validations), generate 3-5 reasonable test cases
              covering the most obvious happy-path and error scenarios.
            - NEVER add fuzzy matching, security header checks, boundary tests, or error
              tests unless the objective explicitly requests them.

            RULES for expectedBodyContains:
            - ALWAYS extract specific values the objective asks you to validate and include
              them in expectedBodyContains. For example, if the objective says "validate NMI
              property set to 6305824657" include "6305824657". If it says "check there is a
              Meter Serial 444444" include "444444".
            - If a discovery response is available above, you may also include field names
              from it — but user-requested validations take priority.
            - Never guess or invent values that are not mentioned in the objective or
              the discovery response.

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

            Generate ONLY the number of test cases needed to faithfully satisfy the objective.
            Respond ONLY with the JSON array.
            """;

        return await AskLlmForJsonAsync<List<ApiTestCase>>(prompt, ct);
    }

    /// <summary>
    /// Execute a single API test case and return a TestStep with the result.
    /// </summary>
    private async Task<TestStep> ExecuteTestCaseAsync(
        ApiTestCase tc, string apiBaseUrl, string? stackKey, string? envKey, CancellationToken ct)
    {
        var stepSw = Stopwatch.StartNew();

        try
        {
            // Build the request
            var url = BuildUrl(tc, apiBaseUrl);
            var request = new HttpRequestMessage(
                new HttpMethod(tc.Method.ToUpperInvariant()), url);

            // Add headers from the generated test case
            foreach (var (key, value) in tc.Headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }

            // Inject auth credentials (overrides anything the LLM generated
            // for the same header, ensuring real credentials are always used).
            await InjectAuthAsync(request, stackKey, envKey, ct);

            // Add body for non-GET requests
            if (tc.Body is not null && tc.Method.ToUpperInvariant() != "GET")
            {
                var json = tc.Body is string s ? s : JsonSerializer.Serialize(tc.Body);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            // Send it
            Logger.LogInformation("[{Agent}] >> {Method} {Url}", Name, tc.Method, url);
            var response = await _http.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            Logger.LogInformation("[{Agent}] << {Status} ({Length} bytes)",
                Name, (int)response.StatusCode, responseBody.Length);

            // Validate the response
            var validation = await ValidateResponseAsync(tc, response, responseBody, ct);

            return new TestStep
            {
                Action = $"{tc.Method} {tc.Endpoint}",
                Summary = $"[{tc.Name}] {validation.Reason}",
                Status = validation.Passed ? TestStatus.Passed : TestStatus.Failed,
                Detail = FormatResponseDetail(url, response, responseBody, request),
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
    private async Task<EndpointDiscovery?> DiscoverEndpointAsync(
        TestTask task, string apiBaseUrl, string? stackKey, string? envKey, CancellationToken ct)
    {
        // Extract endpoint path + optional query string from the task description
        // Matches paths like /Api/Foo?x=1 or Api/Foo?x=1 (with or without leading /)
        var match = System.Text.RegularExpressions.Regex.Match(
            task.Description, @"(/?[\w][\w/\-]*(?:\?[^\s""]+)?)");
        var pathAndQuery = match.Success ? match.Value : string.Empty;
        if (string.IsNullOrEmpty(pathAndQuery)) return null;

        // Ensure leading slash
        if (!pathAndQuery.StartsWith('/')) pathAndQuery = "/" + pathAndQuery;
        var url = $"{apiBaseUrl.TrimEnd('/')}{pathAndQuery}";

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            await InjectAuthAsync(request, stackKey, envKey, ct);

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
    /// Injects auth credentials into the request via the token provider.
    /// Called after the LLM-generated headers are applied so real credentials
    /// always take precedence.
    /// </summary>
    private async Task InjectAuthAsync(HttpRequestMessage request, string? stackKey, string? envKey, CancellationToken ct)
    {
        var tokenProvider = _resolver.GetTokenProvider(stackKey, envKey);
        var token = await tokenProvider.GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token)) return;

        var authHeaderName = _resolver.GetAuthHeaderName(stackKey);
        var authScheme = _resolver.GetAuthScheme(stackKey);

        // Remove any auth header the LLM may have guessed so we don't duplicate it
        request.Headers.Remove(authHeaderName);

        var headerValue = authScheme.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? token                            // raw value  e.g. X-Api-Key: abc
            : $"{authScheme} {token}";         // scheme + token  e.g. Bearer eyJ…

        request.Headers.TryAddWithoutValidation(authHeaderName, headerValue);
    }

    private static string BuildUrl(ApiTestCase tc, string apiBaseUrl)
    {
        var baseUrl = apiBaseUrl.TrimEnd('/');
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
        string requestUrl, HttpResponseMessage response, string body,
        HttpRequestMessage? request = null)
    {
        var reqHeaders = request is not null
            ? string.Join("\n", request.Headers
                .Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                .Select(h => $"  {h.Key}: {string.Join(", ", h.Value)}"))
            : "";
        var respHeaders = string.Join("\n",
            response.Headers.Select(h => $"  {h.Key}: {string.Join(", ", h.Value)}"));
        var truncated = body.Length > 500 ? body[..500] + "..." : body;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Request: {requestUrl}");
        if (!string.IsNullOrEmpty(reqHeaders))
        {
            sb.AppendLine("Request Headers:");
            sb.AppendLine(reqHeaders);
        }
        sb.AppendLine($"Status: {(int)response.StatusCode} {response.ReasonPhrase}");
        sb.AppendLine("Headers:");
        sb.AppendLine(respHeaders);
        sb.AppendLine("Body:");
        sb.Append(truncated);
        return sb.ToString();
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