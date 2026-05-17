namespace AiTestCrew.Core.Configuration;

/// <summary>Jira Xray connection config. Bound from appsettings.json -> TestEnvironment.JiraXray.</summary>
public class JiraXrayConfig
{
    /// <summary>Cloud or Server. Cloud uses Xray JWT; Server/DC uses Jira Basic auth.</summary>
    public string Mode { get; set; } = "Cloud";

    /// <summary>Jira base URL e.g. https://yourtenant.atlassian.net</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Xray Cloud client ID (from Xray API Keys page). Ignored for Server mode.</summary>
    public string XrayClientId { get; set; } = "";

    /// <summary>Xray Cloud client secret. Ignored for Server mode.</summary>
    public string XrayClientSecret { get; set; } = "";

    /// <summary>Jira email for Basic auth (Cloud REST API + Server/DC).</summary>
    public string JiraEmail { get; set; } = "";

    /// <summary>Jira API token or password for Basic auth.</summary>
    public string JiraApiToken { get; set; } = "";
}
