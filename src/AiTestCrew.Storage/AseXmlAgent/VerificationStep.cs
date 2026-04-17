using AiTestCrew.Agents.Shared;

namespace AiTestCrew.Agents.AseXmlAgent;

/// <summary>
/// A post-delivery UI verification attached to an <see cref="AseXmlDeliveryTestDefinition"/>.
///
/// One verification targets one UI surface (Legacy MVC, Blazor, or WinForms) and
/// runs AFTER the XML has been uploaded and a fixed delay has elapsed (giving
/// Bravo time to consume and process the file). Values from the delivery render
/// — NMI, MessageID, TransactionID, filename, any template field — are injected
/// at playback time via <c>{{Token}}</c> substitution on every string field of
/// the UI steps.
///
/// Authoring: via the <c>--record-verification</c> CLI command, which launches
/// the matching recorder and auto-parameterises captured literals that match
/// the delivery's known context values.
/// </summary>
public class VerificationStep
{
    /// <summary>Human-readable label, e.g. "MFN Process Overview shows 'One In All In'".</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Target UI surface. Valid values match <see cref="Core.Models.TestTargetType"/>:
    /// <c>UI_Web_MVC</c>, <c>UI_Web_Blazor</c>, <c>UI_Desktop_WinForms</c>.
    /// </summary>
    public string Target { get; set; } = "UI_Web_Blazor";

    /// <summary>
    /// Fixed delay in seconds between the delivery upload (or the previous verification)
    /// and this verification's first step. Applied cumulatively across verifications.
    /// </summary>
    public int WaitBeforeSeconds { get; set; } = 30;

    /// <summary>Web UI steps — populated when <see cref="Target"/> is <c>UI_Web_*</c>. Null otherwise.</summary>
    public WebUiTestDefinition? WebUi { get; set; }

    /// <summary>Desktop UI steps — populated when <see cref="Target"/> is <c>UI_Desktop_*</c>. Null otherwise.</summary>
    public DesktopUiTestDefinition? DesktopUi { get; set; }
}
