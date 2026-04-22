using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiTestCrew.Runner.AgentMode;

/// <summary>
/// HTTP wrapper for the WebApi's agent + queue endpoints.
/// Authenticates with the owner's API key.
/// </summary>
internal sealed class AgentClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AgentClient(string serverUrl, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public async Task<string> RegisterAsync(string? id, string name, string[] capabilities, string? version)
    {
        var res = await _http.PostAsJsonAsync("api/agents/register",
            new { id, name, capabilities, version }, JsonOpts);
        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<RegisterResponse>(JsonOpts)
            ?? throw new InvalidOperationException("Empty register response");
        return payload.AgentId;
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(string agentId, string status)
    {
        var res = await _http.PostAsJsonAsync($"api/agents/{agentId}/heartbeat",
            new { status }, JsonOpts);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOpts)
            ?? new HeartbeatResponse();
    }

    public async Task DeregisterAsync(string agentId)
    {
        var res = await _http.DeleteAsync($"api/agents/{agentId}");
        if (res.StatusCode != HttpStatusCode.NotFound)
            res.EnsureSuccessStatusCode();
    }

    public async Task<NextJobResponse?> NextJobAsync(string agentId, IEnumerable<string> capabilities)
    {
        var capString = Uri.EscapeDataString(string.Join(",", capabilities));
        var url = $"api/queue/next?agentId={Uri.EscapeDataString(agentId)}&capabilities={capString}";
        var res = await _http.GetAsync(url);
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<NextJobResponse>(JsonOpts);
    }

    public async Task ReportProgressAsync(string jobId)
    {
        var res = await _http.PostAsJsonAsync($"api/queue/{jobId}/progress",
            new { status = "Running" }, JsonOpts);
        res.EnsureSuccessStatusCode();
    }

    public async Task ReportResultAsync(string jobId, bool success, string? error)
    {
        var res = await _http.PostAsJsonAsync($"api/queue/{jobId}/result",
            new { success, error }, JsonOpts);
        res.EnsureSuccessStatusCode();
    }

    private record RegisterResponse(string AgentId);
}

internal sealed class HeartbeatResponse
{
    public string? Status { get; set; }
    public string? ActiveJobId { get; set; }
    public string? ActiveJobStatus { get; set; }
    public bool ShouldExit { get; set; }
}

internal sealed class NextJobResponse
{
    public string JobId { get; set; } = "";
    public string? ModuleId { get; set; }
    public string TestSetId { get; set; } = "";
    public string? ObjectiveId { get; set; }
    public string TargetType { get; set; } = "";
    public string Mode { get; set; } = "";
    public string JobKind { get; set; } = "Run";
    public string RequestJson { get; set; } = "";
}
