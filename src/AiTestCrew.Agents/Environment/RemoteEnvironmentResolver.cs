using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiTestCrew.Agents.Environment;

public sealed class RemoteEnvironmentResolver : IEnvironmentResolver
{
    private readonly IEnvironmentResolver _inner;
    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly string _apiKey;
    private readonly ILogger<RemoteEnvironmentResolver> _logger;

    private readonly ConcurrentDictionary<(string env, string key), string?> _dbCache = new();
    private readonly ConcurrentDictionary<(string env, string key), ServiceBusRemoteDto?> _sbCache = new();

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public RemoteEnvironmentResolver(
        IEnvironmentResolver inner,
        HttpClient http,
        string serverUrl,
        string apiKey,
        ILogger<RemoteEnvironmentResolver> logger)
    {
        _inner     = inner;
        _http      = http;
        _serverUrl = serverUrl.TrimEnd('/');
        _apiKey    = apiKey;
        _logger    = logger;
    }

    public string ResolveKey(string? requested) => _inner.ResolveKey(requested);
    public IReadOnlyCollection<string> ListKeys() => _inner.ListKeys();
    public EnvironmentConfig Resolve(string? key) => _inner.Resolve(key);
    public string ResolveDisplayName(string? key) => _inner.ResolveDisplayName(key);
    public string ResolveLegacyWebUiUrl(string? key) => _inner.ResolveLegacyWebUiUrl(key);
    public string ResolveLegacyWebUiUsername(string? key) => _inner.ResolveLegacyWebUiUsername(key);
    public string ResolveLegacyWebUiPassword(string? key) => _inner.ResolveLegacyWebUiPassword(key);
    public string? ResolveLegacyWebUiStorageStatePath(string? key) => _inner.ResolveLegacyWebUiStorageStatePath(key);
    public string ResolveBraveCloudUiUrl(string? key) => _inner.ResolveBraveCloudUiUrl(key);
    public string ResolveBraveCloudUiUsername(string? key) => _inner.ResolveBraveCloudUiUsername(key);
    public string ResolveBraveCloudUiPassword(string? key) => _inner.ResolveBraveCloudUiPassword(key);
    public string? ResolveBraveCloudUiStorageStatePath(string? key) => _inner.ResolveBraveCloudUiStorageStatePath(key);
    public string? ResolveBraveCloudUiTotpSecret(string? key) => _inner.ResolveBraveCloudUiTotpSecret(key);
    public string ResolveWinFormsAppPath(string? key) => _inner.ResolveWinFormsAppPath(key);
    public string? ResolveWinFormsAppArgs(string? key) => _inner.ResolveWinFormsAppArgs(key);
    public bool ResolveAllowDbDryRun(string? envKey) => _inner.ResolveAllowDbDryRun(envKey);
    public IReadOnlyList<string> ListDbConnectionKeys(string? envKey) => _inner.ListDbConnectionKeys(envKey);
    public bool ResolveAllowEventAssertPeek(string? envKey) => _inner.ResolveAllowEventAssertPeek(envKey);
    public IReadOnlyList<string> ListServiceBusConnectionKeys(string? e) => _inner.ListServiceBusConnectionKeys(e);
    public bool ResolveDataTeardownEnabled(string? key) => _inner.ResolveDataTeardownEnabled(key);
    public bool ResolveRunDataPacksOnStartup(string? key) => _inner.ResolveRunDataPacksOnStartup(key);
    public string ResolveApiStackBaseUrl(string? key, string stackKey) => _inner.ResolveApiStackBaseUrl(key, stackKey);
    public bool ResolveAllowApiDryRun(string? envKey) => _inner.ResolveAllowApiDryRun(envKey);
    public bool ResolveAuthHealthEnabled(string? key) => _inner.ResolveAuthHealthEnabled(key);

    public string ResolveBravoDbConnectionString(string? key)
    {
        var resolved = ResolveDbConnectionString("BravoDb", key);
        return resolved ?? _inner.ResolveBravoDbConnectionString(key);
    }

    public string? ResolveDbConnectionString(string connectionKey, string? envKey)
    {
        var local = _inner.ResolveDbConnectionString(connectionKey, envKey);
        if (!string.IsNullOrWhiteSpace(local)) return local;
        var effectiveEnv = _inner.ResolveKey(envKey);
        var cacheKey = (effectiveEnv, connectionKey);
        if (_dbCache.TryGetValue(cacheKey, out var cached)) return cached;
        var fetched = FetchDbConnectionString(connectionKey, effectiveEnv);
        _dbCache[cacheKey] = fetched;
        return fetched;
    }

    public ServiceBusConnectionConfig? ResolveServiceBusConnection(string connectionKey, string? envKey)
    {
        var local = _inner.ResolveServiceBusConnection(connectionKey, envKey);
        if (local is not null) return local;
        var effectiveEnv = _inner.ResolveKey(envKey);
        var cacheKey = (effectiveEnv, connectionKey);
        if (_sbCache.TryGetValue(cacheKey, out var cached)) return MapToConfig(cached);
        var fetched = FetchServiceBusConfig(connectionKey, effectiveEnv);
        _sbCache[cacheKey] = fetched;
        return MapToConfig(fetched);
    }

    private string? FetchDbConnectionString(string connectionKey, string envKey)
    {
        var url = $"{_serverUrl}/api/environments/{Uri.EscapeDataString(envKey)}/connections/db/{Uri.EscapeDataString(connectionKey)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", _apiKey);
            using var resp = _http.Send(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            { _logger.LogWarning("RER: 404 DB key={Key} env={Env}", connectionKey, envKey); return null; }
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            { _logger.LogWarning("RER: 403 DB key={Key} env={Env}", connectionKey, envKey); return null; }
            if (!resp.IsSuccessStatusCode)
            { _logger.LogWarning("RER: {Status} DB key={Key} env={Env}", (int)resp.StatusCode, connectionKey, envKey); return null; }
            using var stream = resp.Content.ReadAsStream();
            var dto = JsonSerializer.Deserialize<DbConnectionDto>(stream, _json);
            _logger.LogInformation("RER: resolved DB key={Key} env={Env} source={Src}", connectionKey, envKey, dto?.Source ?? "?");
            return dto?.ConnectionString; // value not logged
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RER: network error DB key={Key} env={Env}", connectionKey, envKey); return null; }
    }

    private ServiceBusRemoteDto? FetchServiceBusConfig(string connectionKey, string envKey)
    {
        var url = $"{_serverUrl}/api/environments/{Uri.EscapeDataString(envKey)}/connections/servicebus/{Uri.EscapeDataString(connectionKey)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", _apiKey);
            using var resp = _http.Send(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            { _logger.LogWarning("RER: 404 SB key={Key} env={Env}", connectionKey, envKey); return null; }
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            { _logger.LogWarning("RER: 403 SB key={Key} env={Env}", connectionKey, envKey); return null; }
            if (!resp.IsSuccessStatusCode)
            { _logger.LogWarning("RER: {Status} SB key={Key} env={Env}", (int)resp.StatusCode, connectionKey, envKey); return null; }
            using var stream = resp.Content.ReadAsStream();
            var dto = JsonSerializer.Deserialize<ServiceBusRemoteDto>(stream, _json);
            _logger.LogInformation("RER: resolved SB key={Key} env={Env} authMode={Mode}", connectionKey, envKey, dto?.AuthMode ?? "?");
            return dto;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RER: network error SB key={Key} env={Env}", connectionKey, envKey); return null; }
    }

    private static ServiceBusConnectionConfig? MapToConfig(ServiceBusRemoteDto? dto)
    {
        if (dto is null) return null;
        var authMode = Enum.TryParse<ServiceBusAuthMode>(dto.AuthMode, ignoreCase: true, out var m)
            ? m : ServiceBusAuthMode.ConnectionString;
        return new ServiceBusConnectionConfig
        {
            AuthMode                = authMode,
            ConnectionString        = dto.ConnectionString,
            FullyQualifiedNamespace = dto.FullyQualifiedNamespace,
            ManagedIdentityClientId = dto.ManagedIdentityClientId,
        };
    }

    private sealed class DbConnectionDto
    {
        public string? ConnectionKey    { get; set; }
        public string? ConnectionString { get; set; }
        public string? Source           { get; set; }
    }

    private sealed class ServiceBusRemoteDto
    {
        public string? ConnectionKey            { get; set; }
        public string? AuthMode                 { get; set; }
        public string? ConnectionString         { get; set; }
        public string? FullyQualifiedNamespace  { get; set; }
        public string? ManagedIdentityClientId  { get; set; }
    }
}
