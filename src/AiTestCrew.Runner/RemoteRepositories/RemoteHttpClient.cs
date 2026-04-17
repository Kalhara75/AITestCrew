using System.Net.Http.Json;
using System.Text.Json;

namespace AiTestCrew.Runner.RemoteRepositories;

/// <summary>
/// Shared HTTP client for Runner API client repositories.
/// Injects the X-Api-Key header on every request.
/// </summary>
internal sealed class RemoteHttpClient
{
    private readonly HttpClient _http;

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RemoteHttpClient(string serverUrl, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public async Task<T?> GetAsync<T>(string path)
    {
        var res = await _http.GetAsync(path);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return default;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(JsonOpts);
    }

    public async Task<string?> GetStringOrNullAsync(string path)
    {
        var res = await _http.GetAsync(path);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }

    public async Task PostAsync<T>(string path, T body)
    {
        var res = await _http.PostAsJsonAsync(path, body, JsonOpts);
        res.EnsureSuccessStatusCode();
    }

    public async Task<TResult?> PostAsync<T, TResult>(string path, T body)
    {
        var res = await _http.PostAsJsonAsync(path, body, JsonOpts);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<TResult>(JsonOpts);
    }

    public async Task PutAsync<T>(string path, T body)
    {
        var res = await _http.PutAsJsonAsync(path, body, JsonOpts);
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string path)
    {
        var res = await _http.DeleteAsync(path);
        res.EnsureSuccessStatusCode();
    }
}
