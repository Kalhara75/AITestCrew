using System.IO.Compression;
using System.Text;

namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// Wraps a single XML payload in a ZIP archive — used when the resolved
/// Bravo endpoint has <c>IsOutboundFilesZiped = 1</c>.
/// </summary>
public static class XmlZipPackager
{
    /// <summary>
    /// Produces a seekable <see cref="MemoryStream"/> positioned at zero, containing
    /// a single ZIP entry named <paramref name="entryFileName"/> with the UTF-8
    /// bytes of <paramref name="xmlContent"/>.
    /// </summary>
    public static MemoryStream Package(string xmlContent, string entryFileName)
    {
        if (string.IsNullOrEmpty(entryFileName))
            throw new ArgumentException("entryFileName is required.", nameof(entryFileName));

        // Encode without BOM — AEMO recipients typically expect clean UTF-8.
        var xmlBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xmlContent ?? "");

        var memory = new MemoryStream();
        // leaveOpen so the MemoryStream stays usable after the ZipArchive is disposed.
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryFileName, CompressionLevel.Optimal);
            entry.LastWriteTime = DateTimeOffset.UtcNow;

            using var entryStream = entry.Open();
            entryStream.Write(xmlBytes, 0, xmlBytes.Length);
        }

        memory.Position = 0;
        return memory;
    }
}
