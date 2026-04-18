using System.Net.Http.Headers;
using AiTestCrew.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AiTestCrew.Agents.Shared;

/// <summary>
/// Uploads a locally-captured screenshot to the central server so the dashboard can render it.
/// No-op when <c>TestEnvironmentConfig.ServerUrl</c> is empty (legacy local mode — the UI fetches
/// directly from the server's own screenshot dir).
/// </summary>
internal static class RemoteScreenshotUploader
{
    public static async Task TryUploadAsync(TestEnvironmentConfig config, string localPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(config.ServerUrl)) return;
        if (!File.Exists(localPath)) return;

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/") };
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);

            using var content = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(localPath);
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "file", Path.GetFileName(localPath));

            var res = await http.PostAsync("api/screenshots", content);
            res.EnsureSuccessStatusCode();
            logger.LogInformation("Screenshot uploaded to server: {File}", Path.GetFileName(localPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Screenshot upload failed for {File}: {Msg}", Path.GetFileName(localPath), ex.Message);
        }
    }
}
