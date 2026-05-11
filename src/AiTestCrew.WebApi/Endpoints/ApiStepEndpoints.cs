using System.Text;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Core.Utilities;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class ApiStepEndpoints
{
    private const int DryRunBodyTruncateBytes = 32_768;
    private const int DryRunTimeoutSeconds = 10;

    public static RouteGroupBuilder MapApiStepEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/dry-run", async (
            ApiDryRunRequest? body, HttpContext ctx, IApiTargetResolver resolver,
            IEnvironmentResolver envResolver, DbDryRunRateLimiter rateLimiter,
            IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();
            if (body is null) return Results.BadRequest(new { error = "request body is required" });
            if (string.IsNullOrWhiteSpace(body.Endpoint)) return Results.BadRequest(new { error = "endpoint is required" });
            if (!rateLimiter.TryAcquire(user.Id)) return Results.Json(new { error = "Too many API dry-run requests; try again in a minute." }, statusCode: 429);
            if (!envResolver.ResolveAllowApiDryRun(body.EnvKey)) return Results.Json(new { error = "API dry-run is disabled for env " + (body.EnvKey ?? "default") + "." }, statusCode: 403);
            var paramDict = body.Parameters is null ? new Dictionary<string, string>() : new Dictionary<string, string>(body.Parameters, StringComparer.OrdinalIgnoreCase);
            var method = body.Method?.ToUpperInvariant() ?? "GET";
            var endpoint = TokenSubstituter.Substitute(body.Endpoint, paramDict, throwOnMissing: false) ?? body.Endpoint;
            var baseUrl = resolver.ResolveApiBaseUrl(body.StackKey, body.ModuleKey, body.EnvKey);
            if (string.IsNullOrWhiteSpace(baseUrl)) return Results.NotFound(new { error = "API stack/module not configured for env " + (body.EnvKey ?? "default") + "." });
            var url = baseUrl.TrimEnd(char.Parse("/")) + (endpoint.StartsWith("/") ? endpoint : "/" + endpoint);
            if (body.QueryParams != null && body.QueryParams.Count > 0) {
                var qs = string.Join("&", body.QueryParams.Select(kv => Uri.EscapeDataString(TokenSubstituter.Substitute(kv.Key, paramDict, throwOnMissing: false) ?? kv.Key) + "=" + Uri.EscapeDataString(TokenSubstituter.Substitute(kv.Value, paramDict, throwOnMissing: false) ?? kv.Value)));
                url += "?" + qs;
            }
            var logger = loggerFactory.CreateLogger("ApiStepEndpoints");
            logger.LogInformation("API dry-run: {Method} {Url} (user={UserId}, env={Env})", method, url, user.Id, body.EnvKey ?? "default");
            try {
                using var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(DryRunTimeoutSeconds);
                var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (body.Headers != null) foreach (var (k, v) in body.Headers) { var sk = TokenSubstituter.Substitute(k, paramDict, throwOnMissing: false) ?? k; var sv = TokenSubstituter.Substitute(v, paramDict, throwOnMissing: false) ?? v; request.Headers.TryAddWithoutValidation(sk, sv); }
                var tokenProvider = resolver.GetTokenProvider(body.StackKey, body.EnvKey);
                var token = await tokenProvider.GetTokenAsync(ct);
                if (!string.IsNullOrWhiteSpace(token)) {
                    var authHeaderName = resolver.GetAuthHeaderName(body.StackKey);
                    var authScheme = resolver.GetAuthScheme(body.StackKey);
                    request.Headers.Remove(authHeaderName);
                    request.Headers.TryAddWithoutValidation(authHeaderName, authScheme.Equals("None", StringComparison.OrdinalIgnoreCase) ? token : authScheme + " " + token);
                }
                if (body.Body is not null && method != "GET") {
                    var bodyJson = body.Body is string s ? s : System.Text.Json.JsonSerializer.Serialize(body.Body);
                    var subJson = TokenSubstituter.Substitute(bodyJson, paramDict, throwOnMissing: false) ?? bodyJson;
                    request.Content = new StringContent(subJson, Encoding.UTF8, "application/json");
                }
                var response = await http.SendAsync(request, ct);
                var rawBody = await response.Content.ReadAsStringAsync(ct);
                var truncBody = rawBody.Length > DryRunBodyTruncateBytes ? rawBody[..DryRunBodyTruncateBytes] + "...[truncated]" : rawBody;
                var respHeaders = response.Headers.Concat(response.Content.Headers)
                    .Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
                return Results.Ok(new ApiDryRunResponse(Status: (int)response.StatusCode, ReasonPhrase: response.ReasonPhrase ?? "", Headers: respHeaders, Body: truncBody, BodyTruncated: rawBody.Length > DryRunBodyTruncateBytes));
            } catch (TaskCanceledException) {
                return Results.Json(new { error = "Request timed out after " + DryRunTimeoutSeconds + "s." }, statusCode: 504);
            } catch (Exception ex) {
                logger.LogWarning(ex, "API dry-run failed for {Url}", url);
                return Results.Problem(detail: ex.Message, title: "API dry-run failed", statusCode: 500);
            }
        });
        return group;
    }
}

public class ApiDryRunRequest {
    public string? EnvKey { get; set; }
    public string? StackKey { get; set; }
    public string? ModuleKey { get; set; }
    public string Method { get; set; } = "GET";
    public string Endpoint { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, string>? QueryParams { get; set; }
    public object? Body { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

public record ApiDryRunResponse(int Status, string ReasonPhrase, IReadOnlyDictionary<string, string> Headers, string Body, bool BodyTruncated);