using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// Uploads a payload (XML or zip) to a resolved Bravo endpoint's inbound drop location.
/// Implementations: <see cref="SftpDropTarget"/>, <see cref="FtpDropTarget"/>.
/// </summary>
public interface IXmlDropTarget
{
    Task<DeliveryReceipt> UploadAsync(
        BravoEndpoint endpoint,
        string remoteFileName,
        Stream content,
        CancellationToken ct);
}

/// <summary>
/// Result of a successful upload. Returned to the agent for step reporting.
/// </summary>
public sealed record DeliveryReceipt(
    string RemotePath,
    long BytesWritten,
    TimeSpan Duration);
