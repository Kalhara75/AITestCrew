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
/// </summary>
public sealed class BravoEndpointResolver : IEndpointResolver
{
    private readonly TestEnvironmentConfig _config;
    private readonly ILogger<BravoEndpointResolver> _logger;
    private readonly ConcurrentDictionary<string, BravoEndpoint> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _codeList;
    private readonly SemaphoreSlim _listLock = new(1, 1);

    public BravoEndpointResolver(
        TestEnvironmentConfig config,
        ILogger<BravoEndpointResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<BravoEndpoint?> ResolveAsync(string endpointCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointCode)) return null;
        if (_cache.TryGetValue(endpointCode, out var cached)) return cached;

        EnsureConnectionStringConfigured();

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

        await using var conn = new SqlConnection(_config.AseXml.BravoDb.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@code", System.Data.SqlDbType.NVarChar) { Value = endpointCode });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            _logger.LogWarning("Endpoint '{Code}' not found in mil.V2_MIL_EndPoint", endpointCode);
            return null;
        }

        var endpoint = new BravoEndpoint(
            EndPointCode: reader.GetString(0),
            FtpServer:    reader.IsDBNull(1) ? "" : reader.GetString(1),
            UserName:     reader.IsDBNull(2) ? "" : reader.GetString(2),
            Password:     reader.IsDBNull(3) ? "" : reader.GetString(3),
            OutBoxUrl:    reader.IsDBNull(4) ? "" : reader.GetString(4),
            IsOutboundFilesZipped: !reader.IsDBNull(5) && ReadBool(reader, 5));

        _cache[endpointCode] = endpoint;
        _logger.LogInformation(
            "Resolved endpoint '{Code}' — host={Host}, outbox={OutBox}, zipped={Zipped}",
            endpoint.EndPointCode, endpoint.FtpServer, endpoint.OutBoxUrl, endpoint.IsOutboundFilesZipped);
        return endpoint;
    }

    public async Task<IReadOnlyList<string>> ListCodesAsync(CancellationToken ct = default)
    {
        if (_codeList is not null) return _codeList;

        await _listLock.WaitAsync(ct);
        try
        {
            if (_codeList is not null) return _codeList;

            EnsureConnectionStringConfigured();

            await using var conn = new SqlConnection(_config.AseXml.BravoDb.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT EndPointCode FROM mil.V2_MIL_EndPoint ORDER BY EndPointCode", conn);

            var codes = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) codes.Add(reader.GetString(0));
            }

            _codeList = codes;
            _logger.LogInformation("Loaded {Count} endpoint codes from mil.V2_MIL_EndPoint", codes.Count);
            return _codeList;
        }
        finally
        {
            _listLock.Release();
        }
    }

    private void EnsureConnectionStringConfigured()
    {
        if (string.IsNullOrWhiteSpace(_config.AseXml.BravoDb.ConnectionString))
        {
            throw new InvalidOperationException(
                "AseXml.BravoDb.ConnectionString is not configured. Set it in appsettings.json " +
                "(TestEnvironment.AseXml.BravoDb.ConnectionString) before using the delivery agent.");
        }
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
