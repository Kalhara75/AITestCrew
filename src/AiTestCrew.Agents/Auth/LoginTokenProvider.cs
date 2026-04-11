using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.Auth;

/// <summary>
/// Acquires a JWT by calling the application's login API.
/// Caches the token and refreshes automatically when it expires.
/// </summary>
public class LoginTokenProvider : ITokenProvider
{
    private readonly HttpClient _http;
    private readonly string _loginUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly ILogger<LoginTokenProvider> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public LoginTokenProvider(
        HttpClient httpClient,
        string loginUrl,
        string username,
        string password,
        ILogger<LoginTokenProvider> logger)
    {
        _http = httpClient;
        _loginUrl = loginUrl;
        _username = username;
        _password = password;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        // Fast path: token still valid
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedToken;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedToken;

            return await LoginAsync(ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        _logger.LogInformation("Acquiring JWT from {Url} for user {User}", _loginUrl, _username);

        var body = JsonSerializer.Serialize(new
        {
            UserName = _username,
            Password = _password,
            UserData = new
            {
                additionalProp1 = "string",
                additionalProp2 = "string",
                additionalProp3 = "string"
            }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, _loginUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json-patch+json")
        };

        var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Login failed: {(int)response.StatusCode} {response.StatusCode} — {responseBody}");

        // Parse the JWT from the response.
        // Handle both a raw JWT string and a JSON wrapper (e.g. {"token":"eyJ..."}).
        var token = ExtractToken(responseBody);

        _cachedToken = token;
        _expiresAt = GetTokenExpiry(token);

        _logger.LogInformation("JWT acquired, expires at {Expiry:u}", _expiresAt);
        return token;
    }

    private static string ExtractToken(string responseBody)
    {
        var trimmed = responseBody.Trim().Trim('"');

        // If the response is already a raw JWT (starts with eyJ)
        if (trimmed.StartsWith("eyJ", StringComparison.Ordinal))
            return trimmed;

        // Try to parse as JSON and look for common token field names
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        foreach (var fieldName in new[] { "token", "accessToken", "access_token", "jwt", "Token", "AccessToken" })
        {
            if (root.TryGetProperty(fieldName, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString()!;
        }

        // If the JSON has a single string property, use it
        if (root.ValueKind == JsonValueKind.Object)
        {
            string? singleValue = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    if (singleValue is not null)
                    {
                        singleValue = null;
                        break; // More than one string property, can't guess
                    }
                    singleValue = prop.Value.GetString();
                }
            }
            if (singleValue is not null)
                return singleValue;
        }

        throw new InvalidOperationException(
            $"Unable to extract JWT from login response: {responseBody[..Math.Min(responseBody.Length, 200)]}");
    }

    private DateTimeOffset GetTokenExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return FallbackExpiry();

            // Base64Url decode the payload
            var payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("exp", out var expProp))
            {
                var exp = expProp.GetInt64();
                // Subtract 60 seconds as safety margin
                return DateTimeOffset.FromUnixTimeSeconds(exp) - TimeSpan.FromSeconds(60);
            }

            return FallbackExpiry();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode JWT expiry, using 5-minute fallback TTL");
            return FallbackExpiry();
        }
    }

    private static DateTimeOffset FallbackExpiry()
        => DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
}
