namespace AiTestCrew.Agents.Recording;

/// <summary>Request DTOs serialized into <c>RunQueueEntry.RequestJson</c> for recording jobs.</summary>

/// <summary>Record a standalone UI test case and persist it as a recorded TestObjective.</summary>
public record RecordCaseRequest(
    string ModuleId,
    string TestSetId,
    string CaseName,
    string Target,                   // UI_Web_MVC | UI_Web_Blazor | UI_Desktop_WinForms
    string? EnvironmentKey);

/// <summary>Record reusable setup steps (e.g. login) saved at the test-set level.</summary>
public record RecordSetupRequest(
    string ModuleId,
    string TestSetId,
    string Target,                   // UI_Web_MVC | UI_Web_Blazor (desktop setup not supported today)
    string? EnvironmentKey);

/// <summary>Record a post-delivery UI verification attached to an aseXML delivery objective.</summary>
public record RecordVerificationRequest(
    string ModuleId,
    string TestSetId,
    string ObjectiveId,
    string VerificationName,
    string Target,                   // UI_Web_MVC | UI_Web_Blazor | UI_Desktop_WinForms
    int WaitBeforeSeconds,
    int DeliveryStepIndex,
    string? EnvironmentKey);

/// <summary>Launch an interactive browser login and save the resulting storage state.</summary>
public record AuthSetupRequest(
    string Target,                   // UI_Web_MVC | UI_Web_Blazor
    string? EnvironmentKey);

/// <summary>Outcome of a recording session.</summary>
public record RecordingResult(
    bool Success,
    string Summary,
    int StepsCaptured = 0,
    string? Error = null,
    IReadOnlyList<RecordedStepSummary>? Steps = null);

/// <summary>
/// Flat representation of one captured step for callers (CLI table, agent log).
/// Web and desktop fields are both present; unused ones are null.
/// </summary>
public record RecordedStepSummary(
    string Action,
    string? Selector = null,
    string? AutomationId = null,
    string? Name = null,
    string? Value = null);
