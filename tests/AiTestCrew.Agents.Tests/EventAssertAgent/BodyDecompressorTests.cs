using System.IO.Compression;
using System.Text;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.EventAssertAgent.Body;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.EventAssertAgent;

public class BodyDecompressorTests
{
    private static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gz.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static byte[] RawDeflate(string text)
    {
        using var ms = new MemoryStream();
        using (var def = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            def.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Magic_byte_sniff_decompresses_gzip()
    {
        var compressed = Gzip("{\"InvoiceId\":\"INV-001\"}");
        compressed[0].Should().Be(0x1F);  // gzip magic
        compressed[1].Should().Be(0x8B);

        var result = BodyDecompressor.MaybeDecompress(compressed, applicationProperties: null);

        result.WasDecompressed.Should().BeTrue();
        result.AppliedEncoding.Should().Be("gzip");
        Encoding.UTF8.GetString(result.Body).Should().Be("{\"InvoiceId\":\"INV-001\"}");
    }

    [Fact]
    public void Rebus_app_property_signal_decompresses_gzip()
    {
        var compressed = Gzip("{\"x\":1}");
        var props = new Dictionary<string, object?>
        {
            ["rbs2-content-encoding"] = "gzip",
            ["rbs2-content-type"] = "application/json;charset=utf-8",
        };

        var result = BodyDecompressor.MaybeDecompress(compressed, props);

        result.WasDecompressed.Should().BeTrue();
        result.AppliedEncoding.Should().Be("gzip");
        Encoding.UTF8.GetString(result.Body).Should().Be("{\"x\":1}");
    }

    [Fact]
    public void Standard_Content_Encoding_property_works()
    {
        var compressed = Gzip("{}");
        var props = new Dictionary<string, object?> { ["Content-Encoding"] = "gzip" };

        BodyDecompressor.MaybeDecompress(compressed, props)
            .WasDecompressed.Should().BeTrue();
    }

    [Fact]
    public void App_property_signal_for_deflate_decompresses()
    {
        // Raw deflate (no zlib wrapper) — magic-byte sniff doesn't catch this,
        // but the explicit Content-Encoding=deflate header does.
        var compressed = RawDeflate("hello deflate");
        var props = new Dictionary<string, object?> { ["Content-Encoding"] = "deflate" };

        var result = BodyDecompressor.MaybeDecompress(compressed, props);

        result.WasDecompressed.Should().BeTrue();
        result.AppliedEncoding.Should().Be("deflate");
        Encoding.UTF8.GetString(result.Body).Should().Be("hello deflate");
    }

    [Fact]
    public void Uncompressed_body_passes_through()
    {
        var raw = Encoding.UTF8.GetBytes("{\"x\":1}");
        var result = BodyDecompressor.MaybeDecompress(raw, applicationProperties: null);

        result.WasDecompressed.Should().BeFalse();
        result.AppliedEncoding.Should().BeNull();
        result.Body.Should().BeSameAs(raw);  // returned the same array, no copy
    }

    [Fact]
    public void Empty_body_passes_through()
    {
        var result = BodyDecompressor.MaybeDecompress(Array.Empty<byte>(), null);
        result.WasDecompressed.Should().BeFalse();
        result.Body.Should().BeEmpty();
    }

    [Fact]
    public void Corrupt_gzip_returns_original_bytes_with_failure_marker()
    {
        // Take a valid gzip stream and chop the middle out — header is fine,
        // body is mid-deflate-block junk, decompression throws partway, the
        // helper returns the raw bytes so downstream criteria can surface a
        // typed body-format failure rather than crash the agent.
        var valid = Gzip("the quick brown fox jumps over the lazy dog");
        var corrupt = valid.Take(10).Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }).ToArray();

        var result = BodyDecompressor.MaybeDecompress(corrupt, null);

        result.WasDecompressed.Should().BeFalse();
        result.AppliedEncoding.Should().Be("gzip-failed");
        result.Body.Should().BeSameAs(corrupt);
    }

    [Fact]
    public void Mixed_case_content_encoding_value_handled()
    {
        var compressed = Gzip("ok");
        var props = new Dictionary<string, object?> { ["rbs2-content-encoding"] = "GZIP" };

        BodyDecompressor.MaybeDecompress(compressed, props)
            .WasDecompressed.Should().BeTrue();
    }

    [Fact]
    public void Content_Encoding_with_extra_tokens_still_picks_up_gzip()
    {
        // Some HTTP-style intermediaries emit lists like "gzip, identity".
        var compressed = Gzip("ok");
        var props = new Dictionary<string, object?> { ["Content-Encoding"] = "gzip, identity" };

        BodyDecompressor.MaybeDecompress(compressed, props)
            .WasDecompressed.Should().BeTrue();
    }

    [Fact]
    public void Body_with_only_gzip_magic_byte_in_first_byte_is_NOT_a_false_positive()
    {
        // Single 0x1F byte then anything — gzip header is 2 bytes, not 1.
        var sus = new byte[] { 0x1F, 0x42, 0x43, 0x44 };
        var result = BodyDecompressor.MaybeDecompress(sus, null);
        result.WasDecompressed.Should().BeFalse();
    }

    [Fact]
    public void Compressed_body_can_be_extracted_via_MessageFieldResolver()
    {
        // End-to-end: simulate what AzureServiceBusEventAgent does — decompress
        // once upfront, pass the effective body to MessageFieldResolver, then
        // Body.<jsonpath> resolves cleanly.
        var compressed = Gzip("{\"InvoiceId\":\"INV-42\",\"Amount\":99.50}");
        var view = new ReceivedMessageView
        {
            ContentType = "application/json;charset=utf-8",
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["rbs2-content-encoding"] = "gzip",
            },
            Body = compressed,
        };

        var dec = BodyDecompressor.MaybeDecompress(view.Body, view.ApplicationProperties);
        dec.WasDecompressed.Should().BeTrue();

        var format = BodyFormatDetector.Resolve(BodyFormat.Auto, view.ContentType, dec.Body);
        format.Should().Be(BodyFormat.Json);

        var result = MessageFieldResolver.Resolve(view, "Body.InvoiceId", format, dec.Body);
        result.Status.Should().Be(ExtractStatus.Found);
        result.Value.Should().Be("INV-42");
    }
}
