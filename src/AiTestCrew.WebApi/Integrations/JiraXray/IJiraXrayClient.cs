namespace AiTestCrew.WebApi.Integrations.JiraXray;

public interface IJiraXrayClient
{
    Task<XrayTestDto> GetTestAsync(string ticketKey, CancellationToken ct = default);
}
