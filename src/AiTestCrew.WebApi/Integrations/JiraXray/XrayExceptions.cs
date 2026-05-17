namespace AiTestCrew.WebApi.Integrations.JiraXray;

public class XrayTicketNotFoundException(string key)
    : Exception($"Xray ticket '{key}' was not found (404).") { }

public class XrayAuthException(string message)
    : Exception(message) { }

public class XrayUpstreamException(string message)
    : Exception(message) { }
