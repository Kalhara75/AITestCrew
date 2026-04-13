using System.Diagnostics;
using FluentFTP;
using Microsoft.Extensions.Logging;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// Plain-FTP uploader using FluentFTP. Selected by <see cref="DropTargetFactory"/>
/// when the endpoint's <see cref="BravoEndpoint.OutBoxUrl"/> starts with <c>ftp://</c>.
/// </summary>
public sealed class FtpDropTarget : IXmlDropTarget
{
    private readonly ILogger<FtpDropTarget> _logger;
    private readonly int _timeoutSeconds;

    public FtpDropTarget(ILogger<FtpDropTarget> logger, int timeoutSeconds)
    {
        _logger = logger;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<DeliveryReceipt> UploadAsync(
        BravoEndpoint endpoint,
        string remoteFileName,
        Stream content,
        CancellationToken ct)
    {
        var (host, port) = ParseHostPort(endpoint.FtpServer, defaultPort: 21);
        var remoteDir = ExtractRemotePath(endpoint.OutBoxUrl);
        var remoteFullPath = CombineRemote(remoteDir, remoteFileName);

        _logger.LogInformation(
            "FTP upload — host={Host}:{Port}, user={User}, path={Path}",
            host, port, endpoint.UserName, remoteFullPath);

        var sw = Stopwatch.StartNew();

        using var client = new AsyncFtpClient(host, endpoint.UserName, endpoint.Password, port);
        client.Config.ConnectTimeout = _timeoutSeconds * 1000;
        client.Config.ReadTimeout = _timeoutSeconds * 1000;
        client.Config.DataConnectionConnectTimeout = _timeoutSeconds * 1000;
        client.Config.DataConnectionReadTimeout = _timeoutSeconds * 1000;

        await client.Connect(ct);
        try
        {
            // Ensure directory exists.
            if (!string.IsNullOrEmpty(remoteDir) && !await client.DirectoryExists(remoteDir, ct))
            {
                await client.CreateDirectory(remoteDir, true, ct);
            }

            content.Position = 0;
            var status = await client.UploadStream(
                content,
                remoteFullPath,
                FtpRemoteExists.Overwrite,
                createRemoteDir: true,
                token: ct);

            if (status != FtpStatus.Success)
            {
                throw new IOException($"FTP upload returned status '{status}' for '{remoteFullPath}'.");
            }

            var info = await client.GetObjectInfo(remoteFullPath, token: ct);
            if (info is null)
            {
                throw new IOException($"FTP upload completed but remote file not found at '{remoteFullPath}'.");
            }
        }
        finally
        {
            await client.Disconnect(ct);
        }

        sw.Stop();
        var bytes = content.CanSeek ? content.Length : -1;
        return new DeliveryReceipt(remoteFullPath, bytes, sw.Elapsed);
    }

    private static (string host, int port) ParseHostPort(string ftpServer, int defaultPort)
    {
        var s = (ftpServer ?? "").Trim();
        if (s.Length == 0) throw new ArgumentException("FTPServer is empty.", nameof(ftpServer));

        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];

        var slashIdx = s.IndexOf('/');
        if (slashIdx >= 0) s = s[..slashIdx];

        var colonIdx = s.IndexOf(':');
        if (colonIdx < 0) return (s, defaultPort);

        var host = s[..colonIdx];
        var portText = s[(colonIdx + 1)..];
        return int.TryParse(portText, out var port) ? (host, port) : (host, defaultPort);
    }

    private static string ExtractRemotePath(string outBoxUrl)
    {
        var s = (outBoxUrl ?? "").Trim();
        if (s.Length == 0) return "/";

        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            s = s[(schemeIdx + 3)..];
            var slashIdx = s.IndexOf('/');
            s = slashIdx >= 0 ? s[slashIdx..] : "/";
        }

        if (!s.StartsWith('/')) s = "/" + s;
        return s;
    }

    private static string CombineRemote(string dir, string fileName)
    {
        var d = dir.TrimEnd('/');
        return $"{d}/{fileName}";
    }
}
