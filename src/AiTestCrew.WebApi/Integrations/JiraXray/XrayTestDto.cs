namespace AiTestCrew.WebApi.Integrations.JiraXray;

/// <summary>Normalised Xray test ticket — works for both Cloud and Server shapes.</summary>
public class XrayTestDto
{
    public string Key { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>Raw description text (ADF converted to plain text).</summary>
    public string Description { get; set; } = "";
    public List<string> Labels { get; set; } = [];
    /// <summary>Manual | Cucumber | Generic</summary>
    public string TestType { get; set; } = "Manual";
    /// <summary>Structured steps. Empty for description-driven tests.</summary>
    public List<XrayStep> Steps { get; set; } = [];
    public string? CucumberScenario { get; set; }
    public string? GenericDefinition { get; set; }
    /// <summary>Populated when Steps is empty and Description contains parseable sections.</summary>
    public ParsedXrayDescription? ParsedDescription { get; set; }
}

public class XrayStep
{
    public int Index { get; set; }
    public string Action { get; set; } = "";
    public string? Data { get; set; }
    public string? ExpectedResult { get; set; }
}

public class ParsedXrayDescription
{
    public List<string> Preconditions { get; set; } = [];
    public string? TestData { get; set; }
    /// <summary>Each Expected Outcome bullet is a separate entry — maps to its own AITestCrew step.</summary>
    public List<string> ExpectedOutcomes { get; set; } = [];
    public Dictionary<string, string> OtherSections { get; set; } = new();
}
