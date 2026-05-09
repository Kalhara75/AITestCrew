using System.IO.Compression;

namespace AiTestCrew.Agents.EventAssertAgent.Body;

/// <summary>
/// Detects and decompresses gzip / deflate / zlib body payloads. Common for
/// messages produced by Rebus / NServiceBus / MassTransit transports that
/// auto-compress bodies above a size threshold. Without this step, criteria
/// authored against <c>Body.&lt;jsonpath&gt;</c> would see compressed garbage
/// and fail with typed "not JSON" reasons even though the producer's logical
/// payload IS JSON.
///
/// <para>Detection order:</para>
/// <list type="number">
///   <item><description>Application property
///     <c>rbs2-content-encoding</c> / <c>Content-Encoding</c> /
///     <c>content-encoding</c> — the producer's explicit signal. Rebus sets
///     <c>rbs2-content-encoding=gzip</c> when its compression decorator is
///     active.</description></item>
///   <item><description>Magic-byte sniff — gzip is <c>1F 8B</c> (RFC 1952);
///     zlib-wrapped deflate is <c>78 01 / 78 5E / 78 9C / 78 DA</c>
///     (RFC 1950).</description></item>
/// </list>
///
/// <para>
/// On any failure (corrupt stream, unsupported encoding, etc.) the helper
/// returns the original bytes with <see cref="DecompressionResult.WasDecompressed"/>
/// = false. Consumer code receives the raw garbage rather than silently
/// swallowing data — preserves the diagnostic trail.
/// </para>
/// </summary>
public static class BodyDecompressor
{
    /// <summary>
    /// Returns the effective (post-decompression) body bytes plus a flag
    /// indicating whether decompression was applied. When no compression is
    /// detected or decompression fails, returns the original bytes.
    /// </summary>
    public static DecompressionResult MaybeDecompress(
        byte[] body,
        IReadOnlyDictionary<string, object?>? applicationProperties)
    {
        if (body is null || body.Length == 0)
            return new DecompressionResult(body ?? Array.Empty<byte>(), false, null);

        var declared = ReadEncodingProperty(applicationProperties);
        var sniffed = SniffCompression(body);
        var encoding = declared ?? sniffed;

        return encoding switch
        {
            "gzip" => TryDecompress(body, "gzip", b =>
                new GZipStream(new MemoryStream(b), CompressionMode.Decompress)),
            "deflate" => TryDecompress(body, "deflate", b =>
                new DeflateStream(new MemoryStream(b), CompressionMode.Decompress)),
            "zlib" => TryDecompress(body, "zlib", b =>
                new ZLibStream(new MemoryStream(b), CompressionMode.Decompress)),
            _ => new DecompressionResult(body, false, null),
        };
    }

    private static string? ReadEncodingProperty(IReadOnlyDictionary<string, object?>? props)
    {
        if (props is null || props.Count == 0) return null;
        // Most-specific to least: producers set their own framing first;
        // ASP.NET-style Content-Encoding is the cross-stack default.
        foreach (var name in s_encodingHeaderCandidates)
        {
            if (!props.TryGetValue(name, out var raw) || raw is null) continue;
            var value = raw.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(value)) continue;
            // Direct equality fast path; substring scan handles
            // "gzip, identity" and similar quoted forms.
            if (value == "gzip" || value.Contains("gzip", StringComparison.Ordinal)) return "gzip";
            if (value == "deflate" || value.Contains("deflate", StringComparison.Ordinal)) return "deflate";
        }
        return null;
    }

    private static string? SniffCompression(byte[] body)
    {
        if (body.Length < 2) return null;
        // Gzip — RFC 1952. Magic bytes 1F 8B.
        if (body[0] == 0x1F && body[1] == 0x8B) return "gzip";
        // Zlib — RFC 1950. First byte 0x78; second is one of the four
        // standard FCHECK + FLEVEL combinations. These bytes ARE NOT a
        // unique signature (any short binary blob could begin with them),
        // so this branch is intentionally disabled by default — only the
        // explicit Content-Encoding header triggers zlib decompression.
        // Keeping the magic-byte check disabled avoids false-positive
        // decompression of legitimate binary payloads.
        return null;
    }

    private static DecompressionResult TryDecompress(
        byte[] body, string encoding, Func<byte[], Stream> openStream)
    {
        try
        {
            using var stream = openStream(body);
            using var output = new MemoryStream(capacity: body.Length * 4);
            stream.CopyTo(output);
            return new DecompressionResult(output.ToArray(), true, encoding);
        }
        catch (Exception)
        {
            // Corrupt stream / wrong encoding declared. Return the original
            // bytes so downstream criteria fail with a typed body-format
            // reason instead of crashing the agent.
            return new DecompressionResult(body, false, encoding + "-failed");
        }
    }

    private static readonly string[] s_encodingHeaderCandidates =
    {
        "rbs2-content-encoding",
        "Content-Encoding",
        "content-encoding",
    };
}

/// <summary>
/// Outcome of <see cref="BodyDecompressor.MaybeDecompress"/>. <see cref="Body"/>
/// is the effective body the rest of the pipeline should use; when
/// <see cref="WasDecompressed"/> is false this is the original input.
/// <see cref="AppliedEncoding"/> names what was applied (or what was tried
/// and failed — in which case <see cref="WasDecompressed"/> is also false)
/// for diagnostic surfacing.
/// </summary>
public readonly record struct DecompressionResult(
    byte[] Body, bool WasDecompressed, string? AppliedEncoding);
