using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Exceptions;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.ApiAgent;

public class ApiTestAgent : BaseTestAgent
{
    private readonly HttpClient _http;
    private readonly TestEnvironmentConfig _config;
    private readonly IApiTargetResolver _resolver;
    private const int DefaultPostStepBodyTruncationBytes = 16_384;
    public override string Name => "API Agent";
    public override string Role => "Senior REST API Test Engineer";

    public ApiTestAgent(
        Kernel kernel, ILogger<ApiTestAgent> logger, HttpClient httpClient,
        TestEnvironmentConfig config, IApiTargetResolver resolver,
        PostStepOrchestrator postStepOrchestrator) : base(kernel, logger, postStepOrchestrator)
    { _http = httpClient; _config = config; _resolver = resolver; }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target is TestTargetType.API_REST or TestTargetType.API_GraphQL);

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();
        task.Parameters.TryGetValue("ApiStackKey", out var sk);
        task.Parameters.TryGetValue("ApiModule", out var mk);
        task.Parameters.TryGetValue("EnvironmentKey", out var ek);
        var stackKey = sk as string; var moduleKey = mk as string; var envKey = ek as string;
        var resolvedBaseUrl = _resolver.ResolveApiBaseUrl(stackKey, moduleKey, envKey);
        var envParams = StepParameterSubstituter.ReadEnvironmentParameters(task.Parameters);
        Logger.LogInformation("[{Agent}] Starting task: {Desc} (target: {BaseUrl}, env: {Env})",
            Name, task.Description, resolvedBaseUrl, envKey ?? "default");
        try
        {
            List<ApiTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded) && preloaded is List<ApiTestCase> saved)
            {
                testCases = saved;
                steps.Add(TestStep.Pass("load-cases", $"Loaded {testCases.Count} saved test cases (reuse mode)"));
                Logger.LogInformation("[{Agent}] Reuse mode: using {Count} saved test cases", Name, testCases.Count);
            }
            var verifyOnly = task.Parameters.TryGetValue("VerifyOnly", out var voFlag) && voFlag is true;
            if (verifyOnly)
            {
                if (testCases is null)
                {
                    steps.Add(TestStep.Err("verify-only", "VerifyOnly requires preloaded test cases."));
                    return new TestResult { ObjectiveId = task.Id, ObjectiveName = task.Description, AgentName = Name,
                        Status = TestStatus.Error, Summary = "Missing preloaded test cases for VerifyOnly.", Steps = steps, Duration = sw.Elapsed };
                }
                var substituted = envParams.Count > 0 ? testCases.Select(tc => StepParameterSubstituter.Apply(tc, envParams)).ToList() : testCases;
                return await RunVerifyOnlyAsync(task, substituted, tc => tc.PostSteps, sw, ct);
            }
            if (testCases is null)
            {
                string? apiSpec = null;
                if (!string.IsNullOrEmpty(_config.OpenApiSpecUrl))
                {
                    apiSpec = await TryLoadOpenApiSpecAsync(ct);
                    steps.Add(apiSpec is not null ? TestStep.Pass("load-spec", "Loaded OpenAPI specification") : TestStep.Pass("load-spec", "No OpenAPI spec available, using LLM inference"));
                }
                var discovery = await DiscoverEndpointAsync(task, resolvedBaseUrl, stackKey, envKey, ct);
                if (discovery is not null)
                {
                    steps.Add(TestStep.Pass("discovery", $"Discovery call: {discovery.StatusCode} captured {discovery.BodySample.Length} chars, fields: {discovery.TopLevelFields}"));
                    Logger.LogInformation("[{Agent}] Discovery: {Status}, fields: {Fields}", Name, discovery.StatusCode, discovery.TopLevelFields);
                }
                testCases = await GenerateTestCasesAsync(task, apiSpec, discovery, resolvedBaseUrl, ct);
                if (testCases is null || testCases.Count == 0)
                    return new TestResult { ObjectiveId = task.Id, ObjectiveName = task.Description, AgentName = Name,
                        Status = TestStatus.Error, Summary = "LLM failed to generate test cases", Steps = steps, Duration = sw.Elapsed };
                steps.Add(TestStep.Pass("generate-cases", $"Generated {testCases.Count} test cases"));
                Logger.LogInformation("[{Agent}] Generated {Count} test cases", Name, testCases.Count);
            }
            for (var tcIdx = 0; tcIdx < testCases.Count; tcIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var rawTc = testCases[tcIdx];
                var tc = envParams.Count > 0 ? StepParameterSubstituter.Apply(rawTc, envParams) : rawTc;
                NormaliseLegacyFieldsOnCase(tc);
                if (!await TryPreParentDrainsAsync(tc.PostSteps, tcIdx + 1, steps, envKey, envParams, ct)) continue;
                var (stepResult, capturedTokens) = await ExecuteTestCaseAsync(tc, resolvedBaseUrl, stackKey, envKey, ct);
                steps.Add(stepResult);
                Logger.LogInformation("[{Agent}] {Status}: {Method} {Endpoint} - {Summary}", Name, stepResult.Status, tc.Method, tc.Endpoint, stepResult.Summary);
                if (tc.PostSteps.Count > 0)
                    await RunPostStepsAsync(tc.PostSteps, tc, new List<TestStep> { stepResult }, tcIdx + 1, steps, envKey, envParams, ct, task, capturedTokens: capturedTokens.Count > 0 ? capturedTokens : null);
            }
            var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
            var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
            var hasAwaiting = steps.Any(s => s.Status == TestStatus.AwaitingVerification);
            var status = hasErrors ? TestStatus.Error : hasFails ? TestStatus.Failed : hasAwaiting ? TestStatus.AwaitingVerification : TestStatus.Passed;
            var summary = await SummariseResultsAsync(steps, ct);
            var metadata = new Dictionary<string, object> { ["totalCases"] = testCases.Count, ["baseUrl"] = resolvedBaseUrl, ["generatedTestCases"] = testCases };
            if (stackKey is not null) metadata["apiStack"] = stackKey;
            if (moduleKey is not null) metadata["apiModule"] = moduleKey;
            return new TestResult { ObjectiveId = task.Id, ObjectiveName = task.Description, AgentName = Name, Status = status, Summary = summary, Steps = steps, Duration = sw.Elapsed, Metadata = metadata };
        }
        catch (OperationCanceledException)
        { return new TestResult { ObjectiveId = task.Id, ObjectiveName = task.Description, AgentName = Name, Status = TestStatus.Error, Summary = "Test execution was cancelled", Steps = steps, Duration = sw.Elapsed }; }
        catch (AuthRequiredException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] Unhandled error", Name);
            steps.Add(TestStep.Err("fatal", $"Unhandled exception: {ex.Message}"));
            return new TestResult { ObjectiveId = task.Id, ObjectiveName = task.Description, AgentName = Name, Status = TestStatus.Error, Summary = $"Agent error: {ex.Message}", Steps = steps, Duration = sw.Elapsed };
        }
    }
    private async Task<List<ApiTestCase>?> GenerateTestCasesAsync(TestTask task, string? apiSpec, EndpointDiscovery? discovery, string apiBaseUrl, CancellationToken ct)
    {
        var specSection = apiSpec is not null
            ? "OpenAPI spec:\n" + apiSpec[..Math.Min(apiSpec.Length, 6000)]
            : "No OpenAPI spec available. Infer from task description.";
        var authNote = string.IsNullOrWhiteSpace(_config.AuthToken)
            ? "No authentication configured."
            : $"Auth pre-configured ({_config.AuthScheme} token injected). Do NOT add Authorization header.";
        var discoverySection = discovery is not null
            ? "Discovery response:\nStatus: " + discovery.StatusCode + "\nBody sample:\n" + discovery.BodySample + "\nUse ACTUAL field names."
            : string.Empty;
        var prompt = $$"""
            Generate API test cases for: "{{task.Description}}"
            Base URL: {{apiBaseUrl}}
            {{specSection}}
            {{discoverySection}}
            {{authNote}}
            Rules: read literally. If specific: ONE case. If comprehensive: up to 8. If vague: 3-5.
            Respond ONLY with JSON array:
            [{"name":"test","method":"GET","endpoint":"/api/...","headers":{},"queryParams":{},"body":null,"expectedStatus":200,"expectedBodyContains":[],"expectedBodyNotContains":[],"isFuzzTest":false}]
            """;
        return await AskLlmForJsonAsync<List<ApiTestCase>>(prompt, ct);
    }
    private async Task<(TestStep step, Dictionary<string, string> capturedTokens)> ExecuteTestCaseAsync(
        ApiTestCase tc, string apiBaseUrl, string? stackKey, string? envKey, CancellationToken ct)
    {
        var stepSw = Stopwatch.StartNew();
        var url = BuildUrl(tc, apiBaseUrl);
        var capturedTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<HttpRequestMessage> BuildRequestAsync()
        {
            var req = new HttpRequestMessage(new HttpMethod(tc.Method.ToUpperInvariant()), url);
            foreach (var (key, value) in tc.Headers) req.Headers.TryAddWithoutValidation(key, value);
            await InjectAuthAsync(req, stackKey, envKey, ct);
            if (tc.Body is not null && tc.Method.ToUpperInvariant() != "GET")
            {
                var json = tc.Body is string s ? s : JsonSerializer.Serialize(tc.Body);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            return req;
        }
        try
        {
            Logger.LogInformation("[{Agent}] >> {Method} {Url}", Name, tc.Method, url);
            var request = await BuildRequestAsync();
            var response = await _http.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (_config.Auth.AutoRecoverApi && (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
            {
                Logger.LogWarning("[{Agent}] {Status} for {Url} - invalidating token and retrying once", Name, (int)response.StatusCode, url);
                response.Dispose();
                var tokenProvider = _resolver.GetTokenProvider(stackKey, envKey);
                await tokenProvider.InvalidateAsync(ct);
                request = await BuildRequestAsync();
                response = await _http.SendAsync(request, ct);
                responseBody = await response.Content.ReadAsStringAsync(ct);
                if ((response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) && _config.Auth.PauseOnAuthFailure)
                    throw new AuthRequiredException(envKey ?? "default", AuthSurface.Api, stackKey, $"API returned {(int)response.StatusCode} after token refresh for {tc.Method} {tc.Endpoint}");
            }
            Logger.LogInformation("[{Agent}] << {Status} ({Length} bytes)", Name, (int)response.StatusCode, responseBody.Length);
            var statusCode = (int)response.StatusCode;
            var responseHeaders = BuildHeaderDict(response);
            if (tc.Captures.Count > 0) RunCaptures(tc.Captures, statusCode, responseHeaders, responseBody, capturedTokens);
            ApiValidation validation;
            List<ApiAssertionResult>? assertionResults = null;
            if (tc.ApiAssertions.Count > 0)
                (validation, assertionResults) = EvaluateStructuredAssertions(tc.ApiAssertions, statusCode, responseHeaders, responseBody);
            else
            {
                if (tc.ExpectedStatus != 200 || tc.ExpectedBodyContains.Count > 0 || tc.ExpectedBodyNotContains.Count > 0)
                    Logger.LogWarning("[{Agent}] [{Case}] Legacy fields set but ApiAssertions empty - falling back to LLM. Possible shim regression.", Name, tc.Name);
                validation = await ValidateResponseLlmAsync(tc, response, responseBody, ct);
            }
            return (BuildStep(tc, url, response, responseBody, request, validation, assertionResults, capturedTokens, stepSw.Elapsed), capturedTokens);
        }
        catch (AuthRequiredException) { throw; }
        catch (LoginFailedException ex)
        { return (TestStep.Err($"{tc.Method} {tc.Endpoint}", $"[{tc.Name}] Authentication failed: HTTP {ex.HttpStatusCode} for '{ex.Username}'. Check appsettings.json. Server: {ex.ResponseSnippet}"), capturedTokens); }
        catch (HttpRequestException ex)
        { return (TestStep.Err($"{tc.Method} {tc.Endpoint}", $"[{tc.Name}] Connection failed: {ex.Message}"), capturedTokens); }
        catch (TaskCanceledException)
        { return (TestStep.Err($"{tc.Method} {tc.Endpoint}", $"[{tc.Name}] Request timed out"), capturedTokens); }
    }

    private static (ApiValidation validation, List<ApiAssertionResult> results) EvaluateStructuredAssertions(
        List<ApiAssertion> assertions, int statusCode, Dictionary<string, IEnumerable<string>> headers, string responseBody)
    {
        var results = new List<ApiAssertionResult>(assertions.Count);
        var failCount = 0;
        foreach (var a in assertions)
        {
            var r = ApiAssertionEvaluator.Evaluate(a, statusCode, headers, responseBody);
            results.Add(new ApiAssertionResult { Source = a.Source.ToString(), HeaderName = a.HeaderName, JsonPath = a.JsonPath, Operator = a.Operator.ToString(), Expected = a.Expected, Actual = r.Actual, Passed = r.Passed, Reason = r.Reason });
            if (!r.Passed) failCount++;
        }
        var reason = failCount == 0 ? $"All {assertions.Count} API assertion(s) passed" : failCount == 1 ? results.First(x => !x.Passed).Reason ?? "1 API assertion failed" : $"{failCount} of {assertions.Count} API assertions failed";
        return (new ApiValidation { Passed = failCount == 0, Reason = reason }, results);
    }

    private void RunCaptures(List<ApiCapture> captures, int statusCode, Dictionary<string, IEnumerable<string>> headers, string responseBody, Dictionary<string, string> capturedTokens)
    {
        foreach (var cap in captures)
        {
            if (string.IsNullOrWhiteSpace(cap.As)) continue;
            string? value = null; string? failReason = null;
            switch (cap.Source)
            {
                case ApiAssertionSource.Status: value = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                case ApiAssertionSource.Header:
                {
                    var hn = cap.HeaderName ?? "";
                    foreach (var (k, vs) in headers) if (string.Equals(k, hn, StringComparison.OrdinalIgnoreCase)) { value = string.Join(", ", vs); break; }
                    if (value is null) failReason = $"Capture '{cap.As}': header '{hn}' not present in response";
                    break;
                }
                case ApiAssertionSource.Body:
                {
                    var path = cap.JsonPath ?? "$";
                    var st = JsonValueExtractor.TryExtract(string.IsNullOrEmpty(responseBody) ? null : responseBody, path, out var node, out var err);
                    switch (st)
                    {
                        case JsonValueExtractor.ExtractionStatus.Found: value = JsonValueExtractor.ToScalarString(node!); break;
                        case JsonValueExtractor.ExtractionStatus.FoundNull: value = ""; break;
                        default: failReason = $"Capture '{cap.As}': {err ?? $"JSON path '{path}' not found"}"; break;
                    }
                    break;
                }
                case ApiAssertionSource.BodyText: value = responseBody ?? ""; break;
            }
            if (failReason is not null) { Logger.LogWarning("[{Agent}] {Reason}", Name, failReason); continue; }
            capturedTokens[cap.As] = value ?? "";
        }
    }

    private TestStep BuildStep(ApiTestCase tc, string url, HttpResponseMessage response, string responseBody, HttpRequestMessage request, ApiValidation validation, List<ApiAssertionResult>? assertionResults, Dictionary<string, string> capturedTokens, TimeSpan duration)
    {
        var step = new TestStep { Action = $"{tc.Method} {tc.Endpoint}", Summary = $"[{tc.Name}] {validation.Reason}", Status = validation.Passed ? TestStatus.Passed : TestStatus.Failed, Detail = FormatResponseDetail(url, response, responseBody, request), Duration = duration };
        if (assertionResults is not null)
        {
            step.Metadata["apiAssertions"] = assertionResults;
            var bodySnippet = responseBody.Length > 4096 ? responseBody[..4096] + "...[truncated]" : responseBody;
            step.Metadata["apiResponse"] = new ApiResponseSnapshot { Status = (int)response.StatusCode, Headers = response.Headers.Take(50).ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase), Body = bodySnippet };
        }
        if (capturedTokens.Count > 0) step.Metadata["capturedTokens"] = capturedTokens;
        return step;
    }

    private async Task<ApiValidation> ValidateResponseLlmAsync(ApiTestCase tc, HttpResponseMessage response, string body, CancellationToken ct)
    {
        var issues = new List<string>();
        var statusCode = (int)response.StatusCode;
        if (statusCode != tc.ExpectedStatus) issues.Add($"Expected status {tc.ExpectedStatus}, got {statusCode}");
        foreach (var expected in tc.ExpectedBodyContains)
            if (!body.Contains(expected, StringComparison.OrdinalIgnoreCase)) issues.Add($"Response body missing: '{expected}'");
        foreach (var unexpected in tc.ExpectedBodyNotContains)
            if (body.Contains(unexpected, StringComparison.OrdinalIgnoreCase)) issues.Add($"Response body contains unexpected: '{unexpected}'");
        if (issues.Count > 0) return new ApiValidation { Passed = false, Reason = string.Join("; ", issues), Issues = issues };
        var truncatedBody = body.Length > 2000 ? body[..2000] + "...[truncated]" : body;
        var securityNotes = new List<string>();
        if (!response.Headers.Contains("X-Content-Type-Options")) securityNotes.Add("Missing X-Content-Type-Options");
        if (!response.Headers.Contains("X-Frame-Options")) securityNotes.Add("Missing X-Frame-Options");
        if (!response.Headers.Contains("Strict-Transport-Security")) securityNotes.Add("Missing HSTS");
        var securityNote = securityNotes.Count > 0 ? $"Advisory (do NOT fail): {string.Join(", ", securityNotes)}" : "Security headers present.";
        var prompt = $$"""
            Validate this API response:
            Request: {{tc.Method}} {{tc.Endpoint}}
            Expected Status: {{tc.ExpectedStatus}} Actual Status: {{statusCode}}
            Response Body: {{truncatedBody}}
            Security notes (advisory, do not fail): {{securityNote}}
            Check: 1) Well-formed JSON? 2) Reasonable data? 3) Correct field types? 4) Error messages/stacks? 5) Sensitive data exposure?
            Base verdict ONLY on status code and body. Respond: { "passed": true/false, "reason": "1-2 sentences", "issues": [] }
            """;
        var llmResult = await AskLlmForJsonAsync<ApiValidation>(prompt, ct);
        return llmResult ?? new ApiValidation { Passed = true, Reason = "Rule checks passed, LLM unavailable" };
    }

    private static void NormaliseLegacyFieldsOnCase(ApiTestCase tc)
    {
        if (tc.ApiAssertions.Count > 0) return;
        if (tc.ExpectedStatus == 200 && tc.ExpectedBodyContains.Count == 0 && tc.ExpectedBodyNotContains.Count == 0) return;
        tc.ApiAssertions.Add(new ApiAssertion { Source = ApiAssertionSource.Status, Operator = AssertionOperator.Equals, Expected = tc.ExpectedStatus.ToString() });
        foreach (var expr in tc.ExpectedBodyContains) tc.ApiAssertions.Add(new ApiAssertion { Source = ApiAssertionSource.BodyText, Operator = AssertionOperator.Contains, Expected = expr, IgnoreCase = true });
        foreach (var expr in tc.ExpectedBodyNotContains) tc.ApiAssertions.Add(new ApiAssertion { Source = ApiAssertionSource.BodyText, Operator = AssertionOperator.NotContains, Expected = expr, IgnoreCase = true });
    }

    private static Dictionary<string, IEnumerable<string>> BuildHeaderDict(HttpResponseMessage response)
    {
        var dict = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in response.Headers) dict[k] = v;
        if (response.Content?.Headers is not null) foreach (var (k, v) in response.Content.Headers) dict[k] = v;
        return dict;
    }

    private async Task<EndpointDiscovery?> DiscoverEndpointAsync(TestTask task, string apiBaseUrl, string? stackKey, string? envKey, CancellationToken ct)
    {
        var match = System.Text.RegularExpressions.Regex.Match(task.Description, @"(/?[\w][\w/\-]*(?:\?[^\s]+)?)");
        var pathAndQuery = match.Success ? match.Value : string.Empty;
        if (string.IsNullOrEmpty(pathAndQuery)) return null;
        if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;
        var url = string.Concat(apiBaseUrl.TrimEnd(char.Parse("/")), pathAndQuery);
        try {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            await InjectAuthAsync(request, stackKey, envKey, ct);
            Logger.LogDebug("[{Agent}] Discovery GET {Url}", Name, url);
            var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var topLevelFields = string.Empty;
            try {
                using var doc = System.Text.Json.JsonDocument.Parse(body.Length > 0 ? body : "{}");
                var root = doc.RootElement;
                topLevelFields = root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() > 0
                    ? string.Join(", ", root[0].EnumerateObject().Select(p => p.Name))
                    : root.ValueKind == System.Text.Json.JsonValueKind.Object ? string.Join(", ", root.EnumerateObject().Select(p => p.Name)) : "(non-JSON)";
            } catch { topLevelFields = "(could not parse JSON)"; }
            var headers = string.Join(", ", response.Headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key + ": " + string.Join(",", h.Value)));
            return new EndpointDiscovery(StatusCode: (int)response.StatusCode, Headers: headers, BodySample: body.Length > 1500 ? body[..1500] + "...[truncated]" : body, TopLevelFields: topLevelFields);
        } catch (Exception ex) { Logger.LogWarning("[{Agent}] Discovery failed for {Url}: {Err}", Name, url, ex.Message); return null; }
    }

    private async Task InjectAuthAsync(HttpRequestMessage request, string? stackKey, string? envKey, CancellationToken ct)
    {
        var tokenProvider = _resolver.GetTokenProvider(stackKey, envKey);
        var token = await tokenProvider.GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token)) return;
        var authHeaderName = _resolver.GetAuthHeaderName(stackKey);
        var authScheme = _resolver.GetAuthScheme(stackKey);
        request.Headers.Remove(authHeaderName);
        request.Headers.TryAddWithoutValidation(authHeaderName, authScheme.Equals("None", StringComparison.OrdinalIgnoreCase) ? token : authScheme + " " + token);
    }

    private static string BuildUrl(ApiTestCase tc, string apiBaseUrl)
    {
        var baseUrl = apiBaseUrl.TrimEnd("/"[0]);
        var endpoint = tc.Endpoint.StartsWith("/") ? tc.Endpoint : "/" + tc.Endpoint;
        var url = baseUrl + endpoint;
        if (tc.QueryParams.Count > 0)
        {
            var qs = string.Join("&", tc.QueryParams.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            url = url + "?" + qs;
        }
        return url;
    }

    private async Task<string?> TryLoadOpenApiSpecAsync(CancellationToken ct)
    {
        try {
            var spec = await _http.GetStringAsync(_config.OpenApiSpecUrl, ct);
            Logger.LogInformation("[{Agent}] Loaded OpenAPI spec ({Length} chars)", Name, spec.Length);
            return spec;
        } catch (Exception ex) { Logger.LogWarning("[{Agent}] Could not load OpenAPI spec: {Err}", Name, ex.Message); return null; }
    }

    private static string FormatResponseDetail(string requestUrl, HttpResponseMessage response, string body, HttpRequestMessage? request = null)
    {
        var reqHeaders = request is not null
            ? string.Join("\n", request.Headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Select(h => "  " + h.Key + ": " + string.Join(", ", h.Value)))
            : "";
        var respHeaders = string.Join("\n", response.Headers.Select(h => "  " + h.Key + ": " + string.Join(", ", h.Value)));
        var truncated = body.Length > 500 ? body[..500] + "..." : body;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Request: " + requestUrl);
        if (!string.IsNullOrEmpty(reqHeaders)) { sb.AppendLine("Request Headers:"); sb.AppendLine(reqHeaders); }
        sb.AppendLine("Status: " + (int)response.StatusCode + " " + response.ReasonPhrase);
        sb.AppendLine("Headers:"); sb.AppendLine(respHeaders);
        sb.AppendLine("Body:"); sb.Append(truncated);
        return sb.ToString();
    }

    protected override string PostStepParentKind => "Api";

    protected override IDictionary<string, string> BuildPostStepContext(object parentTestCase, IReadOnlyList<TestStep> parentSteps)
    {
        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parentTestCase is ApiTestCase tc)
        {
            ctx["Method"] = tc.Method; ctx["Endpoint"] = tc.Endpoint; ctx["ParentCaseName"] = tc.Name;
        }
        foreach (var step in parentSteps)
        {
            if (step.Metadata.TryGetValue("apiResponse", out var snapRaw) && snapRaw is ApiResponseSnapshot snap)
            {
                ctx["ResponseStatus"] = snap.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ctx["ResponseBody"] = snap.Body.Length > DefaultPostStepBodyTruncationBytes ? snap.Body[..DefaultPostStepBodyTruncationBytes] + "...[truncated]" : snap.Body;
                foreach (var (headerKey, headerVal) in snap.Headers) ctx["ResponseHeader." + headerKey.ToLowerInvariant()] = headerVal;
                break;
            }
            if (!ctx.ContainsKey("ResponseStatus") && step.Detail is not null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(step.Detail, @"Status:\s*(\d{3})");
                if (m.Success) ctx["ResponseStatus"] = m.Groups[1].Value;
            }
        }
        return ctx;
    }
}

internal sealed record EndpointDiscovery(int StatusCode, string Headers, string BodySample, string TopLevelFields);

public class ApiAssertionResult
{
    public string Source { get; set; } = "";
    public string? HeaderName { get; set; }
    public string? JsonPath { get; set; }
    public string Operator { get; set; } = "";
    public string Expected { get; set; } = "";
    public string? Actual { get; set; }
    public bool Passed { get; set; }
    public string? Reason { get; set; }
}

public class ApiResponseSnapshot
{
    public int Status { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; set; } = "";
}
