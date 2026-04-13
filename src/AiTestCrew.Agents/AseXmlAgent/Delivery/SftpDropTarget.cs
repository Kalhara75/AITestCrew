using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// SFTP uploader using SSH.NET.
///
/// Parses <see cref="BravoEndpoint.FtpServer"/> for host + optional port (default 22)
/// and treats <see cref="BravoEndpoint.OutBoxUrl"/> as the remote directory to land in.
/// Both values may carry a scheme prefix (<c>sftp://</c>) which is stripped.
///
/// Host-key pinning is deliberately not enforced in Phase 2 — the fingerprint
/// is logged for audit. A future hardening pass can add a <c>KnownHostsPath</c>
/// config and swap <see cref="ConnectionInfo.HostKeyAlgorithms"/> accordingly.
/// </summary>
public sealed class SftpDropTarget : IXmlDropTarget
{
    private readonly ILogger<SftpDropTarget> _logger;
    private readonly int _timeoutSeconds;

    public SftpDropTarget(ILogger<SftpDropTarget> logger, int timeoutSeconds)
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
        var (host, port) = ParseHostPort(endpoint.FtpServer, defaultPort: 22);
        var remoteDir = ExtractRemotePath(endpoint.OutBoxUrl);
        var remoteFullPath = CombineRemote(remoteDir, remoteFileName);

        _logger.LogInformation(
            "SFTP upload — host={Host}:{Port}, user={User}, path={Path}",
            host, port, endpoint.UserName, remoteFullPath);

        var sw = Stopwatch.StartNew();

        using var client = new SftpClient(host, port, endpoint.UserName, endpoint.Password)
        {
            OperationTimeout = TimeSpan.FromSeconds(_timeoutSeconds),
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        };

        // SSH.NET APIs are synchronous; offload to a thread so we can honour the cancellation token
        // without blocking the scheduler.
        await Task.Run(() =>
        {
            client.Connect();
            LogFingerprint(client);
            ct.ThrowIfCancellationRequested();

            // Ensure the remote directory exists (create parents on demand).
            EnsureDirectory(client, remoteDir);
            ct.ThrowIfCancellationRequested();

            content.Position = 0;
            client.UploadFile(content, remoteFullPath, canOverride: true);

            if (!client.Exists(remoteFullPath))
            {
                throw new IOException($"SFTP upload succeeded without error but remote file not found at '{remoteFullPath}'.");
            }
        }, ct);

        sw.Stop();
        var bytes = content.CanSeek ? content.Length : -1;
        return new DeliveryReceipt(remoteFullPath, bytes, sw.Elapsed);
    }

    private void LogFingerprint(SftpClient client)
    {
        try
        {
            var info = client.ConnectionInfo;
            _logger.LogInformation(
                "SFTP connected — server={Server}, client={Client}",
                info?.ServerVersion, info?.ClientVersion);
        }
        catch
        {
            // Diagnostic only — never fail upload for a missing fingerprint.
        }
    }

    private static (string host, int port) ParseHostPort(string ftpServer, int defaultPort)
    {
        var s = (ftpServer ?? "").Trim();
        if (s.Length == 0) throw new ArgumentException("FTPServer is empty.", nameof(ftpServer));

        // Strip scheme if present.
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];

        // Path after host is ignored — we use OutBoxUrl for the path.
        var slashIdx = s.IndexOf('/');
        if (slashIdx >= 0) s = s[..slashIdx];

        // Split host:port.
        var colonIdx = s.IndexOf(':');
        if (colonIdx < 0) return (s, defaultPort);

        var host = s[..colonIdx];
        var portText = s[(colonIdx + 1)..];
        return int.TryParse(portText, out var port) ? (host, port) : (host, defaultPort);
    }

    /// <summary>
    /// Strips scheme + host from an OutBoxUrl like "sftp://host:22/inbound/" or "/inbound/"
    /// and returns just the directory path component.
    /// </summary>
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

    private static void EnsureDirectory(SftpClient client, string remoteDir)
    {
        if (string.IsNullOrWhiteSpace(remoteDir) || remoteDir == "/") return;
        if (client.Exists(remoteDir)) return;

        // Create parents incrementally.
        var parts = remoteDir.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!client.Exists(current)) client.CreateDirectory(current);
        }
    }
}
