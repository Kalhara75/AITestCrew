using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AiTestCrew.Agents.DesktopUiBase;

/// <summary>
/// OCR over a rectangular region of the screen using the OS-resident
/// <see cref="OcrEngine"/> (Windows.Media.Ocr — no model files to ship,
/// no third-party packages). Used to read text from owner-drawn UI
/// surfaces (legacy WinForms grids, custom-rendered cells) where the
/// UI Automation tree exposes the element but property access throws
/// <c>NotSupportedException</c> on Name / ValuePattern.
///
/// Construction does not validate the engine — callers must check
/// <see cref="IsAvailable"/> before relying on it. When the OS doesn't
/// have an English OCR language pack installed, <see cref="IsAvailable"/>
/// is <c>false</c> and callers should degrade gracefully (warn at
/// recording time, treat replay as a non-OCR assert-text).
/// </summary>
public sealed class WindowsOcrService
{
    private readonly OcrEngine? _engine;
    private readonly ILogger _logger;

    public WindowsOcrService(ILogger logger)
    {
        _logger = logger;
        // Prefer en-US — every Windows install with OCR support has it.
        // Fall back to whatever the user profile offers (handles non-English locales).
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (_engine is null)
            _logger.LogWarning(
                "[OCR] No OCR engine available. Install a Windows OCR language pack (Settings → Time & Language → Language → Add a language → Optional features → OCR) to enable assert-text-ocr.");
    }

    public bool IsAvailable => _engine is not null;

    /// <summary>
    /// Capture <paramref name="width"/> × <paramref name="height"/> pixels
    /// starting at screen position <paramref name="screenX"/>,
    /// <paramref name="screenY"/> and run OCR over them. Returns the
    /// concatenated recognised text (newlines preserved between lines)
    /// or an empty string when nothing was detected.
    /// </summary>
    public async Task<string> RecognizeRegionAsync(
        int screenX, int screenY, int width, int height,
        CancellationToken ct = default)
    {
        if (_engine is null) return "";
        if (width <= 0 || height <= 0) return "";

        try
        {
            // Capture the region. PixelFormat.Format32bppArgb is what
            // BitmapDecoder needs to round-trip cleanly via PNG.
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                gfx.CopyFromScreen(screenX, screenY, 0, 0, new Size(width, height));
            }

            // PNG-round-trip into a SoftwareBitmap. Cleaner than direct
            // pixel-buffer conversion (no manual stride math).
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            var ras = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ras))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask(ct);
                writer.DetachStream();
            }
            ras.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct);

            var result = await _engine.RecognizeAsync(softwareBitmap).AsTask(ct);
            return (result.Text ?? "").Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[OCR] RecognizeRegionAsync failed at ({X},{Y}) {W}x{H}: {Msg}",
                screenX, screenY, width, height, ex.Message);
            return "";
        }
    }

    /// <summary>
    /// Synchronous wrapper for callers that can't await (e.g. hot paths in the
    /// recorder's main loop). Blocks the caller until OCR completes or the
    /// optional <paramref name="ct"/> is cancelled.
    /// </summary>
    public string RecognizeRegion(
        int screenX, int screenY, int width, int height,
        CancellationToken ct = default)
        => RecognizeRegionAsync(screenX, screenY, width, height, ct).GetAwaiter().GetResult();
}
