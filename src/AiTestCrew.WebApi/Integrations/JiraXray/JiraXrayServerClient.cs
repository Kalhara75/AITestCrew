using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiTestCrew.Core.Configuration;

namespace AiTestCrew.WebApi.Integrations.JiraXray;

public sealed class JiraXrayServerClient : IJiraXrayClient
{
    private readonly HttpClient _http;
    private readonly JiraXrayConfig _cfg;
    private readonly string _basicAuth;

    public JiraXrayServerClient(HttpClient http, JiraXrayConfig cfg)
    {
        _http = http; _cfg = cfg;
        _basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(_cfg.JiraEmail + ":" + _cfg.JiraApiToken));
    }

    public async Task<XrayTestDto> GetTestAsync(string ticketKey, CancellationToken ct = default)
    {
        using var jiraReq = new HttpRequestMessage(HttpMethod.Get,
            _cfg.BaseUrl.TrimEnd('/') + "/rest/api/2/issue/" + ticketKey + "?fields=summary,description,labels");
        jiraReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);
        using var jiraResp = await _http.SendAsync(jiraReq, ct);

        if (jiraResp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new XrayTicketNotFoundException(ticketKey);
        if (jiraResp.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new XrayAuthException("Jira 401.");
        if (!jiraResp.IsSuccessStatusCode) throw new XrayUpstreamException("Jira " + (int)jiraResp.StatusCode);

        using var jiraDoc = JsonDocument.Parse(await jiraResp.Content.ReadAsStringAsync(ct));
        var fields = jiraDoc.RootElement.GetProperty("fields");
        var summary = fields.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "";
        var desc = fields.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "";
        var labels = new List<string>();
        if (fields.TryGetProperty("labels", out var labelsEl))
            foreach (var l in labelsEl.EnumerateArray())
                if (l.GetString() is string ls) labels.Add(ls);

        var parsed = XrayDescriptionParser.Parse(desc);

        using var xrayReq = new HttpRequestMessage(HttpMethod.Get,
            _cfg.BaseUrl.TrimEnd('/') + "/rest/raven/2.0/api/test/" + ticketKey + "/step");
        xrayReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);
        using var xrayResp = await _http.SendAsync(xrayReq, ct);

        var steps = new List<XrayStep>();
        if (xrayResp.IsSuccessStatusCode)
        {
            using var xrayDoc = JsonDocument.Parse(await xrayResp.Content.ReadAsStringAsync(ct));
            int idx = 1;
            foreach (var s in xrayDoc.RootElement.EnumerateArray())
            {
                steps.Add(new XrayStep
                {
                    Index = idx++,
                    Action = s.TryGetProperty("step", out var a) && a.TryGetProperty("raw", out var ar) ? ar.GetString() ?? "" : "",
                    Data = s.TryGetProperty("data", out var d) && d.TryGetProperty("raw", out var dr) ? dr.GetString() : null,
                    ExpectedResult = s.TryGetProperty("result", out var r) && r.TryGetProperty("raw", out var rr) ? rr.GetString() : null,
                });
            }
        }

        return new XrayTestDto
        {
            Key = ticketKey, Summary = summary, Description = desc,
            Labels = labels, TestType = "Manual", Steps = steps,
            ParsedDescription = parsed,
        };
    }
}