using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiTestCrew.Core.Configuration;

namespace AiTestCrew.WebApi.Integrations.JiraXray;

public sealed class JiraXrayCloudClient : IJiraXrayClient
{
    private readonly HttpClient _http;
    private readonly JiraXrayConfig _cfg;
    private readonly ILogger<JiraXrayCloudClient> _logger;
    private string? _xrayJwt;
    private DateTime _xrayJwtExpiry = DateTime.MinValue;

    public JiraXrayCloudClient(HttpClient http, JiraXrayConfig cfg, ILogger<JiraXrayCloudClient> logger)
    { _http = http; _cfg = cfg; _logger = logger; }

    public async Task<XrayTestDto> GetTestAsync(string ticketKey, CancellationToken ct = default)
    {
        await EnsureXrayJwtAsync(ct);
        var basicCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes(_cfg.JiraEmail + ":" + _cfg.JiraApiToken));
        var jiraUrl = _cfg.BaseUrl.TrimEnd('/') + "/rest/api/3/issue/" + ticketKey + "?fields=summary,description,labels";
        using var jiraReq = new HttpRequestMessage(HttpMethod.Get, jiraUrl);
        jiraReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCreds);
        using var jiraResp = await _http.SendAsync(jiraReq, ct);

        if (jiraResp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new XrayTicketNotFoundException(ticketKey);
        if (jiraResp.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new XrayAuthException("Jira 401.");
        if (!jiraResp.IsSuccessStatusCode) throw new XrayUpstreamException("Jira " + (int)jiraResp.StatusCode);

        using var jiraDoc = JsonDocument.Parse(await jiraResp.Content.ReadAsStringAsync(ct));
        var fields = jiraDoc.RootElement.GetProperty("fields");
        var summary = fields.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "";
        var descRaw = fields.TryGetProperty("description", out var descEl) ? descEl.GetRawText() : "";
        var labels = new List<string>();
        if (fields.TryGetProperty("labels", out var labelsEl))
            foreach (var l in labelsEl.EnumerateArray())
                if (l.GetString() is string ls) labels.Add(ls);

        var descPlain = NormaliseAdfToText(descRaw);
        var parsed = XrayDescriptionParser.Parse(string.IsNullOrEmpty(descPlain) ? descRaw : descPlain);

        using var xrayReq = new HttpRequestMessage(HttpMethod.Get,
            "https://xray.cloud.getxray.app/api/v2/test?jql=issuekey%3D" + ticketKey);
        xrayReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _xrayJwt);
        using var xrayResp = await _http.SendAsync(xrayReq, ct);

        var steps = new List<XrayStep>();
        string testType = "Manual";

        if (xrayResp.IsSuccessStatusCode)
        {
            using var xrayDoc = JsonDocument.Parse(await xrayResp.Content.ReadAsStringAsync(ct));
            foreach (var testEl in xrayDoc.RootElement.EnumerateArray())
            {
                if (testEl.TryGetProperty("testType", out var tt)) testType = tt.GetString() ?? "Manual";
                if (testEl.TryGetProperty("steps", out var stepsEl))
                {
                    int idx = 1;
                    foreach (var s in stepsEl.EnumerateArray())
                    {
                        steps.Add(new XrayStep
                        {
                            Index = idx++,
                            Action = s.TryGetProperty("action", out var a) ? StripHtml(a.GetString() ?? "") : "",
                            Data = s.TryGetProperty("data", out var d) ? StripHtml(d.GetString() ?? "") : null,
                            ExpectedResult = s.TryGetProperty("result", out var r) ? StripHtml(r.GetString() ?? "") : null,
                        });
                    }
                }
                break;
            }
        }
        else { _logger.LogWarning("Xray {Status} for {Key}.", (int)xrayResp.StatusCode, ticketKey); }

        return new XrayTestDto { Key = ticketKey, Summary = summary, Description = descPlain, Labels = labels, TestType = testType, Steps = steps, ParsedDescription = parsed };
    }

    private async Task EnsureXrayJwtAsync(CancellationToken ct)
    {
        if (_xrayJwt is not null && DateTime.UtcNow < _xrayJwtExpiry) return;
        var body = JsonSerializer.Serialize(new { client_id = _cfg.XrayClientId, client_secret = _cfg.XrayClientSecret });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://xray.cloud.getxray.app/api/v2/authenticate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new XrayAuthException("Xray 401.");
        if (!resp.IsSuccessStatusCode) throw new XrayUpstreamException("Xray auth " + (int)resp.StatusCode);
        _xrayJwt = (await resp.Content.ReadAsStringAsync(ct)).Trim('"');
        _xrayJwtExpiry = DateTime.UtcNow.AddMinutes(55);
    }

    private static string NormaliseAdfToText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith('{')) return raw ?? "";
        try { var sb = new StringBuilder(); ExtractAdfText(JsonDocument.Parse(raw).RootElement, sb); return sb.ToString().Trim(); }
        catch { return raw; }
    }

    private static void ExtractAdfText(JsonElement el, StringBuilder sb)
    {
        if (el.ValueKind == JsonValueKind.Array) { foreach (var i in el.EnumerateArray()) ExtractAdfText(i, sb); return; }
        if (el.ValueKind != JsonValueKind.Object) return;
        var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "text" && el.TryGetProperty("text", out var txt)) { sb.Append(txt.GetString()); return; }
        if (type is "heading" or "paragraph" or "listItem") { if (el.TryGetProperty("content", out var c)) ExtractAdfText(c, sb); sb.AppendLine(); return; }
        if (el.TryGetProperty("content", out var ch)) ExtractAdfText(ch, sb);
    }

    private static string StripHtml(string html) =>
        string.IsNullOrEmpty(html) ? html : System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "").Trim();
}