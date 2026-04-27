using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Shared;

namespace AiTestCrew.Agents.AseXmlAgent;

/// <summary>
/// A post-step attached to a parent test step. Runs AFTER the parent step
/// completes and an optional wait elapses, receiving the parent's context
/// values via <c>{{Token}}</c> substitution on every string field.
///
/// Originally introduced for post-delivery UI verifications on aseXML
/// deliveries (hence the legacy name); since Slice 1 of the generalized
/// post-step work, any parent step (Web UI, Desktop UI, API, aseXML
/// generate, aseXML deliver) can own a list of these. Consequently the
/// carrier fields have grown beyond UI — API, aseXML deliver, and DB check
/// are all valid post-step payloads.
///
/// Exactly ONE carrier field should be populated, matched to <see cref="Target"/>.
///
/// Authoring for delivery parents: via <c>--record-verification</c> CLI.
/// Authoring for other parents will arrive in Slice 2 (<c>--record-post-step</c>).
/// </summary>
public class VerificationStep
{
    /// <summary>Human-readable label, e.g. "MFN Process Overview shows 'One In All In'".</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Target type string matching <see cref="Core.Models.TestTargetType"/>:
    /// <c>UI_Web_MVC</c>, <c>UI_Web_Blazor</c>, <c>UI_Desktop_WinForms</c>,
    /// <c>API_REST</c>, <c>AseXml_Generate</c>, <c>AseXml_Deliver</c>, <c>Db_SqlServer</c>.
    /// </summary>
    public string Target { get; set; } = "UI_Web_Blazor";

    /// <summary>
    /// Fixed delay in seconds between the parent step completing (or the previous
    /// post-step) and this post-step's first action. Applied cumulatively across
    /// post-steps. When larger than the configured defer threshold, the post-step
    /// is queued for later execution by a remote agent instead of blocking inline.
    /// </summary>
    public int WaitBeforeSeconds { get; set; } = 30;

    /// <summary>
    /// Role label used by the UI to differentiate checks from side-effect actions.
    /// Either <c>"Verification"</c> (default, back-compat) or <c>"Action"</c>.
    /// Purely informational — the runtime doesn't key behaviour off this.
    /// </summary>
    public string Role { get; set; } = "Verification";

    /// <summary>Web UI payload — populated when <see cref="Target"/> is <c>UI_Web_*</c>. Null otherwise.</summary>
    public WebUiTestDefinition? WebUi { get; set; }

    /// <summary>Desktop UI payload — populated when <see cref="Target"/> is <c>UI_Desktop_*</c>. Null otherwise.</summary>
    public DesktopUiTestDefinition? DesktopUi { get; set; }

    /// <summary>API payload — populated when <see cref="Target"/> is <c>API_REST</c>. Null otherwise. (Runtime wiring arrives in Slice 2.)</summary>
    public ApiTestDefinition? Api { get; set; }

    /// <summary>aseXML generate payload — populated when <see cref="Target"/> is <c>AseXml_Generate</c>. Null otherwise.</summary>
    public AseXmlTestDefinition? AseXml { get; set; }

    /// <summary>aseXML deliver payload — populated when <see cref="Target"/> is <c>AseXml_Deliver</c>. Null otherwise.</summary>
    public AseXmlDeliveryTestDefinition? AseXmlDeliver { get; set; }

    /// <summary>DB check payload — populated when <see cref="Target"/> is <c>Db_SqlServer</c>. Null otherwise. (Runtime agent arrives in Slice 2.)</summary>
    public DbCheckStepDefinition? DbCheck { get; set; }
}
