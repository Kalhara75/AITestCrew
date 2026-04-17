namespace AiTestCrew.Agents.ApiAgent;

/// <summary>
/// The definition of an API test — HTTP request details and expected response.
/// Used inside <see cref="AiTestCrew.Agents.Persistence.TestObjective"/> for API targets.
/// </summary>
public class ApiTestDefinition
{
    public string Method { get; set; } = "GET";
    public string Endpoint { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
    public Dictionary<string, string> QueryParams { get; set; } = [];
    public object? Body { get; set; }
    public int ExpectedStatus { get; set; } = 200;
    public List<string> ExpectedBodyContains { get; set; } = [];
    public List<string> ExpectedBodyNotContains { get; set; } = [];
    public bool IsFuzzTest { get; set; }

    /// <summary>
    /// Creates an <see cref="ApiTestDefinition"/> from a legacy <see cref="ApiTestCase"/>.
    /// </summary>
    public static ApiTestDefinition FromTestCase(ApiTestCase tc) => new()
    {
        Method = tc.Method,
        Endpoint = tc.Endpoint,
        Headers = tc.Headers,
        QueryParams = tc.QueryParams,
        Body = tc.Body,
        ExpectedStatus = tc.ExpectedStatus,
        ExpectedBodyContains = tc.ExpectedBodyContains,
        ExpectedBodyNotContains = tc.ExpectedBodyNotContains,
        IsFuzzTest = tc.IsFuzzTest
    };

    /// <summary>
    /// Creates an <see cref="ApiTestCase"/> from this definition (for agent execution).
    /// </summary>
    public ApiTestCase ToTestCase(string name) => new()
    {
        Name = name,
        Method = Method,
        Endpoint = Endpoint,
        Headers = Headers,
        QueryParams = QueryParams,
        Body = Body,
        ExpectedStatus = ExpectedStatus,
        ExpectedBodyContains = ExpectedBodyContains,
        ExpectedBodyNotContains = ExpectedBodyNotContains,
        IsFuzzTest = IsFuzzTest
    };
}
