using Microsoft.Extensions.Logging;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// Chooses the right <see cref="IXmlDropTarget"/> for an endpoint based on the
/// scheme in <see cref="BravoEndpoint.OutBoxUrl"/>. Default is SFTP when no
/// scheme is present — Bravo's current convention.
/// </summary>
public sealed class DropTargetFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestEnvironmentConfig _config;

    public DropTargetFactory(ILoggerFactory loggerFactory, TestEnvironmentConfig config)
    {
        _loggerFactory = loggerFactory;
        _config = config;
    }

    public IXmlDropTarget Create(BravoEndpoint endpoint)
    {
        var scheme = DetectScheme(endpoint.OutBoxUrl, endpoint.FtpServer);
        var timeout = _config.AseXml.DeliveryTimeoutSeconds;

        return scheme switch
        {
            "ftp"  => new FtpDropTarget(_loggerFactory.CreateLogger<FtpDropTarget>(), timeout),
            _      => new SftpDropTarget(_loggerFactory.CreateLogger<SftpDropTarget>(), timeout),
        };
    }

    public static string DetectScheme(string outBoxUrl, string ftpServer)
    {
        foreach (var value in new[] { outBoxUrl, ftpServer })
        {
            var s = (value ?? "").Trim().ToLowerInvariant();
            if (s.StartsWith("sftp://", StringComparison.Ordinal)) return "sftp";
            if (s.StartsWith("ftp://",  StringComparison.Ordinal)) return "ftp";
        }
        // Default to SFTP per Bravo's convention.
        return "sftp";
    }
}
