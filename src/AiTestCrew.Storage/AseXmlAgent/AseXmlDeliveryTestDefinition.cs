namespace AiTestCrew.Agents.AseXmlAgent;

/// <summary>
/// The definition of an aseXML delivery test step — template + user field
/// values + the Bravo endpoint to ship the rendered XML to.
/// Stored inside <see cref="AiTestCrew.Agents.Persistence.TestObjective.AseXmlDeliverySteps"/>.
///
/// Rendered XML is freshly produced on each run (see Phase 1 notes) so that
/// re-running a saved test set always produces a unique MessageID.
/// </summary>
public class AseXmlDeliveryTestDefinition
{
    public string Description { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TransactionType { get; set; } = "";
    public Dictionary<string, string> FieldValues { get; set; } = [];

    /// <summary>
    /// Matches <c>mil.V2_MIL_EndPoint.EndPointCode</c>. Empty at save time is
    /// allowed — the CLI's <c>--endpoint</c> flag or a test-set-level default
    /// may supply it at run time.
    /// </summary>
    public string EndpointCode { get; set; } = "";

    public bool ValidateAgainstSchema { get; set; }

    /// <summary>
    /// Optional UI verification steps that run AFTER the XML has been uploaded
    /// and a fixed wait has elapsed. Each step can target a different UI
    /// surface (Legacy MVC, Blazor, WinForms) and receives the delivery's
    /// resolved field values via <c>{{Token}}</c> substitution at playback.
    /// Empty list means upload-only.
    /// </summary>
    public List<VerificationStep> PostDeliveryVerifications { get; set; } = [];

    public AseXmlDeliveryTestCase ToTestCase(string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? Description : name,
        Description = Description,
        TemplateId = TemplateId,
        TransactionType = TransactionType,
        FieldValues = FieldValues,
        EndpointCode = EndpointCode,
        ValidateAgainstSchema = ValidateAgainstSchema,
        PostDeliveryVerifications = PostDeliveryVerifications
    };

    public static AseXmlDeliveryTestDefinition FromTestCase(AseXmlDeliveryTestCase tc) => new()
    {
        Description = tc.Description,
        TemplateId = tc.TemplateId,
        TransactionType = tc.TransactionType,
        FieldValues = tc.FieldValues,
        EndpointCode = tc.EndpointCode,
        ValidateAgainstSchema = tc.ValidateAgainstSchema,
        PostDeliveryVerifications = tc.PostDeliveryVerifications
    };
}

/// <summary>
/// Runtime-mutable counterpart to <see cref="AseXmlDeliveryTestDefinition"/>.
/// </summary>
public class AseXmlDeliveryTestCase
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TransactionType { get; set; } = "";
    public Dictionary<string, string> FieldValues { get; set; } = [];
    public string EndpointCode { get; set; } = "";
    public bool ValidateAgainstSchema { get; set; }
    public List<VerificationStep> PostDeliveryVerifications { get; set; } = [];
}
