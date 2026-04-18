namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Resolves aseXML delivery endpoints from their <see cref="BravoEndpoint.EndPointCode"/>.
/// Concrete implementations typically query the Bravo application DB
/// (<c>mil.V2_MIL_EndPoint</c>) and cache results for the process lifetime.
/// </summary>
public interface IEndpointResolver
{
    /// <summary>
    /// Returns the endpoint matching <paramref name="endpointCode"/>, or <c>null</c>
    /// if no row is found. Implementations must never throw on a simple miss —
    /// reserve exceptions for infrastructure failures (e.g. DB unreachable).
    /// </summary>
    Task<BravoEndpoint?> ResolveAsync(string endpointCode, CancellationToken ct = default);

    /// <summary>
    /// Environment-aware overload. Uses the Bravo DB connection string configured
    /// under <c>Environments[env].BravoDbConnectionString</c> when set, falling
    /// back to <c>AseXml.BravoDb.ConnectionString</c>.
    /// </summary>
    Task<BravoEndpoint?> ResolveAsync(string endpointCode, string? environmentKey, CancellationToken ct = default);

    /// <summary>
    /// Returns all known <c>EndPointCode</c> values. Used by the delivery agent
    /// to seed the LLM with the catalogue of valid endpoints and by the CLI's
    /// <c>--list-endpoints</c> command.
    /// </summary>
    Task<IReadOnlyList<string>> ListCodesAsync(CancellationToken ct = default);

    /// <summary>Environment-aware overload; see <see cref="ResolveAsync(string, string?, CancellationToken)"/>.</summary>
    Task<IReadOnlyList<string>> ListCodesAsync(string? environmentKey, CancellationToken ct = default);
}

/// <summary>
/// Resolved endpoint details for a Bravo outbound drop location.
/// <see cref="Password"/> must never be logged or surfaced in step details.
/// </summary>
public sealed record BravoEndpoint(
    string EndPointCode,
    string FtpServer,
    string UserName,
    string Password,
    string OutBoxUrl,
    bool IsOutboundFilesZipped);
