using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// Resolves endpoints by querying <c>mil.V2_MIL_EndPoint</c> in the Bravo
/// application DB. Results are cached in-memory for the lifetime of the
/// process — restart the Runner/WebApi to pick up DB changes.
/// Per-environment connection strings are supplied via
/// <see cref="IEnvironmentResolver.ResolveBravoDbConnectionString"/> so each
/// customer environment hits its own Bravo DB; caches are keyed per env.
/// </summary>
public sealed class BravoEndpointResolver : IEndpointResolver
{
    private readonly TestEnvironmentConfig _config;
    private readonly IEnvironmentResolver _envResolver;
    private readonly ILogger<BravoEndpointResolver> _logger;
    private readonly ConcurrentDictionary<string, BravoEndpoint> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _codeListByEnv =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _listLock = new(1, 1);

    public BravoEndpointResolver(
        TestEnvironmentConfig config,
        IEnvironmentResolver envResolver,
        ILogger<BravoEndpointResolver> logger)
    {
        _config = config;
        _envResolver = envResolver;
        _logger = logger;
    }

    public Task<BravoEndpoint?> ResolveAsync(string endpointCode, CancellationToken ct = default)
        => ResolveAsync(endpointCode, null, ct);

    public async Task<BravoEndpoint?> ResolveAsync(string endpointCode, string? environmentKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointCode)) return null;

        var resolvedEnv = _envResolver.ResolveKey(environmentKey);
        var cacheKey = $"{resolvedEnv}|{endpointCode}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var connectionString = ResolveConnectionString(resolvedEnv);

        const string sql = """
            SELECT TOP 1
                EndPointCode,
                FTPServer,
                UserName,
                Password,
                OutBoxUrl,
                IsOutboundFilesZiped
            FROM mil.V2_MIL_EndPoint
            WHERE EndPointCode = @code
            """;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@code", System.Data.SqlDbType.NVarChar) { Value = endpointCode });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            _logger.LogWarning("Endpoint '{Code}' not found in mil.V2_MIL_EndPoint (env={Env})",
                endpointCode, resolvedEnv);
            return null;
        }

        var endpoint = new BravoEndpoint(
            EndPointCode: reader.GetString(0),
            FtpServer:    reader.IsDBNull(1) ? "" : reader.GetString(1),
            UserName:     reader.IsDBNull(2) ? "" : reader.GetString(2),
            Password:     reader.IsDBNull(3) ? "" : reader.GetString(3),
            OutBoxUrl:    reader.IsDBNull(4) ? "" : reader.GetString(4),
            IsOutboundFilesZipped: !reader.IsDBNull(5) && ReadBool(reader, 5));

        _cache[cacheKey] = endpoint;
        _logger.LogInformation(
            "Resolved endpoint '{Code}' (env={Env}) — host={Host}, outbox={OutBox}, zipped={Zipped}",
            endpoint.EndPointCode, resolvedEnv, endpoint.FtpServer, endpoint.OutBoxUrl, endpoint.IsOutboundFilesZipped);
        return endpoint;
    }

    public Task<IReadOnlyList<string>> ListCodesAsync(CancellationToken ct = default)
        => ListCodesAsync(null, ct);

    public async Task<IReadOnlyList<string>> ListCodesAsync(string? environmentKey, CancellationToken ct = default)
    {
        var resolvedEnv = _envResolver.ResolveKey(environmentKey);
        if (_codeListByEnv.TryGetValue(resolvedEnv, out var cached)) return cached;

        await _listLock.WaitAsync(ct);
        try
        {
            if (_codeListByEnv.TryGetValue(resolvedEnv, out cached)) return cached;

            var connectionString = ResolveConnectionString(resolvedEnv);

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT EndPointCode FROM mil.V2_MIL_EndPoint ORDER BY EndPointCode", conn);

            var codes = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) codes.Add(reader.GetString(0));
            }

            _codeListByEnv[resolvedEnv] = codes;
            _logger.LogInformation("Loaded {Count} endpoint codes from mil.V2_MIL_EndPoint (env={Env})",
                codes.Count, resolvedEnv);
            return codes;
        }
        finally
        {
            _listLock.Release();
        }
    }

    private string ResolveConnectionString(string envKey)
    {
        var conn = _envResolver.ResolveBravoDbConnectionString(envKey);
        if (string.IsNullOrWhiteSpace(conn))
        {
            throw new InvalidOperationException(
                "Bravo DB connection string is not configured. Set it either under " +
                $"TestEnvironment.Environments.{envKey}.BravoDbConnectionString or " +
                "TestEnvironment.AseXml.BravoDb.ConnectionString.");
        }
        return conn;
    }

    // The column is declared as BIT in SQL Server but drivers sometimes surface
    // it as Int32 (from views/joins), so handle both to stay defensive.
    private static bool ReadBool(SqlDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            int i => i != 0,
            short s => s != 0,
            byte by => by != 0,
            string str => string.Equals(str, "1", StringComparison.Ordinal)
                          || string.Equals(str, "true", StringComparison.OrdinalIgnoreCase),
            _ => Convert.ToBoolean(value),
        };
    }
}
