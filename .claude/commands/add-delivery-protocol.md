Scaffold a new `IXmlDropTarget` implementation so the aseXML delivery agent can upload to a new protocol (AS2, HTTP POST, SMB, GPG-encrypted SFTP, etc.).

The delivery subsystem is split into three independent pieces — endpoint resolution (`IEndpointResolver`), payload packaging (`XmlZipPackager`), and upload (`IXmlDropTarget`). Adding a new protocol means adding a new `IXmlDropTarget` and a dispatch arm in `DropTargetFactory`. The delivery agent, resolver, packager, and orchestrator are untouched.

Arguments: $ARGUMENTS
Expected format: `<scheme> "<description>"`
Examples:
- `as2 "Applicability Statement 2 inbound delivery"`
- `https "HTTPS POST with bearer token"`
- `smb "Windows file share drop"`

## What you must do

Adding a new delivery protocol is **one new class + one `switch` arm + one DI line**. If you find yourself editing `AseXmlDeliveryAgent.cs`, `BravoEndpointResolver.cs`, `XmlZipPackager.cs`, or the delivery persistence models, stop and reconsider — the extension seam lives in `DropTargetFactory` and `IXmlDropTarget`.

### Step 1 — Confirm the endpoint shape works

All endpoints resolve through `BravoEndpointResolver` from `mil.V2_MIL_EndPoint`. That table provides:
- `EndPointCode` (string)
- `FTPServer` (string — historically a hostname; may include scheme or port)
- `UserName` (string)
- `Password` (string)
- `OutBoxUrl` (string — may include a scheme like `sftp://`, `ftp://`, or your new one)
- `IsOutboundFilesZiped` (bit)

If the new protocol needs **additional connection fields** not in this table (e.g. AS2 certificate fingerprint, OAuth token URL, SMB share name), you have two options:

**A. Extend `BravoEndpoint` and the resolver (preferred).** Add the new field(s) to:
- The DB query in `BravoEndpointResolver.ResolveAsync` (add columns to the `SELECT` list).
- The `BravoEndpoint` record in `src/AiTestCrew.Core/Interfaces/IEndpointResolver.cs`.
- The `ReadBool` / `AsString` helpers in the resolver if parsing is non-trivial.
- `docs/architecture.md` (document the new column).

**B. Add config-level overrides in `AseXmlConfig`.** If the value is per-environment rather than per-endpoint, don't extend the DB record — add a `Dictionary<string, ProtocolConfig>` keyed by scheme to `AseXmlConfig`. Read from config inside your new `IXmlDropTarget`.

Pick A when every endpoint row legitimately needs the value. Pick B when it's a deployment-level knob (e.g. a default AS2 certificate shared across endpoints).

### Step 2 — Read the engine's contracts

Read these before writing code:

- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/IXmlDropTarget.cs` — the interface you're implementing, plus `DeliveryReceipt` record.
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/SftpDropTarget.cs` — reference implementation. Note the pattern: parse host/port → parse remote path → ensure remote directory → upload → verify file exists → return receipt.
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/FtpDropTarget.cs` — the second reference, slightly different wrap pattern (FluentFTP async).
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/DropTargetFactory.cs` — the factory with scheme detection.

### Step 3 — Pick a NuGet package (if needed)

| Protocol | Typical package | Notes |
|---|---|---|
| AS2 | `Sebatec.AS2` / `BouncyCastle` | Needs cert handling. Commercial options exist. |
| HTTP / HTTPS POST | Built-in `HttpClient` | No new NuGet. Reuse `IHttpClientFactory` from DI. |
| SMB | `SmbLibrary` / `SMBLibrary.Client` | Or native `System.IO` over a mapped drive (simpler; needs machine setup). |
| GPG-encrypted SFTP | `PgpCore` + existing `SSH.NET` | Encrypt before upload; wrap existing `SftpDropTarget`. |
| Azure Blob / S3 | `Azure.Storage.Blobs` / `AWSSDK.S3` | Credentials via env vars or config. |

Add the package to `src/AiTestCrew.Agents/AiTestCrew.Agents.csproj`. Pin the version and restore via `dotnet restore --source https://api.nuget.org/v3/index.json` if the private Brave feed is unreachable.

### Step 4 — Create the drop-target class

Path: `src/AiTestCrew.Agents/AseXmlAgent/Delivery/{Scheme}DropTarget.cs`

Follow the structural template (based on `SftpDropTarget`):

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// &lt;Description from args&gt;.
/// Selected by <see cref="DropTargetFactory"/> when OutBoxUrl starts with "&lt;scheme&gt;://".
/// </summary>
public sealed class {Scheme}DropTarget : IXmlDropTarget
{
    private readonly ILogger<{Scheme}DropTarget> _logger;
    private readonly int _timeoutSeconds;
    // + any additional dependencies (HttpClient, config, etc.)

    public {Scheme}DropTarget(ILogger<{Scheme}DropTarget> logger, int timeoutSeconds)
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
        // 1. Parse endpoint → connection parameters (host, port, path, credentials).
        // 2. Log at INFO: host + user + path + scheme. NEVER the password.
        // 3. Stopwatch-wrap the transport call.
        // 4. On success: return new DeliveryReceipt(remotePath, bytes, duration).
        // 5. On failure: throw IOException or protocol-specific exception with a
        //    clear message — the delivery agent surfaces it as a Failed step.
    }

    // Private helpers — mirror the host/port + path parsing in SftpDropTarget.
}
```

Rules the implementation must respect:

- **Never log the password.** `endpoint.Password` is OK to use for auth, but never put it in a log line or exception message.
- **Verify the file landed.** After the upload "succeeds", confirm via a second protocol call (e.g. SFTP `Exists`, FTP `GetObjectInfo`, HTTP `HEAD`, etc.) — hides transport-layer lies.
- **Respect the cancellation token.** Long-running uploads must honour `ct` so the overall test step respects `AseXml.DeliveryTimeoutSeconds`.
- **Content is a seekable `MemoryStream`.** Caller sets `Position = 0` before passing; re-set if your protocol consumes and needs to retry.
- **Return `DeliveryReceipt.BytesWritten`** from the actual bytes sent (`content.Length`, typically). Consumers log this into the step summary.

### Step 5 — Wire into the factory

Edit `src/AiTestCrew.Agents/AseXmlAgent/Delivery/DropTargetFactory.cs`:

```csharp
public IXmlDropTarget Create(BravoEndpoint endpoint)
{
    var scheme = DetectScheme(endpoint.OutBoxUrl, endpoint.FtpServer);
    var timeout = _config.AseXml.DeliveryTimeoutSeconds;

    return scheme switch
    {
        "ftp"          => new FtpDropTarget(...),
        "{scheme}"     => new {Scheme}DropTarget(_loggerFactory.CreateLogger<{Scheme}DropTarget>(), timeout),
        _              => new SftpDropTarget(...),      // default
    };
}

public static string DetectScheme(string outBoxUrl, string ftpServer)
{
    foreach (var value in new[] { outBoxUrl, ftpServer })
    {
        var s = (value ?? "").Trim().ToLowerInvariant();
        if (s.StartsWith("sftp://"))      return "sftp";
        if (s.StartsWith("ftp://"))       return "ftp";
        if (s.StartsWith("{scheme}://"))  return "{scheme}";
    }
    return "sftp";  // default unchanged
}
```

**Default stays SFTP.** Only add your scheme to the explicit prefix list — Bravo data should dictate which protocol is used per endpoint.

### Step 6 — DI (only if your drop target has non-logger dependencies)

If your drop target needs something beyond `ILogger` + `timeoutSeconds` (e.g. `HttpClient`, extra config), update the factory's constructor to accept it and thread in from DI. For simple cases (`HttpClient`), register via `IHttpClientFactory` in both `Runner/Program.cs` and `WebApi/Program.cs`.

For most protocols no new DI lines are needed — the factory is already registered as singleton and its constructor already takes `ILoggerFactory + TestEnvironmentConfig`.

### Step 7 — Smoke test

```bash
# Seed / update a row in Bravo's mil.V2_MIL_EndPoint so some EndPointCode uses
# the new scheme in its OutBoxUrl (e.g. "as2://example.com/inbound/").

# Discover the endpoint
dotnet run --project src/AiTestCrew.Runner -- --list-endpoints

# Deliver against it
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset delivery-smoke-<scheme> \
  --endpoint <YourNewEndpointCode> \
  --obj-name "Delivery smoke over <scheme>" \
  "<Generate + deliver objective>"
```

Verify in the output:
- `resolve-endpoint[1]` step shows the resolved endpoint (host, outbox, zipped flag).
- `upload[1]` step summary names the scheme in uppercase — e.g. `Uploaded xml via AS2 → ...`. This comes from `DropTargetFactory.DetectScheme` being called from the agent's log line.
- Local debug XML under `output/asexml/{timestamp}_{taskId}_deliver/` matches the delivered payload.
- Log file shows NO password values (grep for the password you configured — it must not appear).
- Out-of-band on the remote: confirm the file actually landed with the right content.

### Step 8 — Update documentation

- `docs/architecture.md` — extend the "Adding a new delivery protocol" row if you introduced a novel pattern (e.g. "certs live in config not DB").
- `docs/functional.md` — the "Deliver aseXML transactions" section mentions SFTP/FTP auto-detection; add a sentence covering the new scheme.
- `CLAUDE.md` — the "Where to extend" table is already correct; only update if the new protocol changes the extension shape.
- If you extended `BravoEndpoint` with new columns, document the `mil.V2_MIL_EndPoint` schema expectation.

### Step 9 — Do NOT do these things

- Do NOT modify `AseXmlDeliveryAgent.cs` to special-case your protocol. The agent calls `dropFactory.Create(endpoint).UploadAsync(...)` — everything protocol-specific lives inside the target class.
- Do NOT log or surface `endpoint.Password`. Grep your implementation for `.Password` and ensure every usage is a credential hand-off, not a `_logger` or `.Detail`.
- Do NOT hand-craft the `remoteFileName` — it's decided by the agent from `{MessageID}.xml` or `{MessageID}.zip`. Your job is transport.
- Do NOT bypass `DropTargetFactory.DetectScheme`. If you need a non-URL-scheme disambiguator (e.g. port-based detection), discuss first — scheme detection is the public contract.
- Do NOT introduce a second `XmlZipPackager` equivalent. The agent already decides zip-vs-raw based on `endpoint.IsOutboundFilesZipped` before calling the drop target.

### Architecture constraints to respect

- The `IXmlDropTarget` surface is `UploadAsync(endpoint, remoteFileName, content, ct) → DeliveryReceipt`. Keep it.
- Drop targets are stateless per-call — constructed fresh by the factory for each upload. Don't cache connections across calls unless the protocol explicitly benefits (e.g. HTTP/2 with `IHttpClientFactory`).
- The agent stays ignorant of which protocol ran. Scheme surfaces only in log / step summaries as an uppercase tag, via `DropTargetFactory.DetectScheme`.
- New protocols do not change the `VerificationStep` wait model — delivery is still a single synchronous upload from the agent's perspective; waiting happens afterwards.
