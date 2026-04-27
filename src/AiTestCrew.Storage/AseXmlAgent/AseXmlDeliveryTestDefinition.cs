using System.Text.Json.Serialization;

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
    /// Optional post-steps (sub-actions / sub-verifications) that run AFTER
    /// the XML has been uploaded and a fixed wait has elapsed. Each step can
    /// target a different UI surface, DB check, API, aseXML generation/delivery
    /// and receives the delivery's resolved field values via <c>{{Token}}</c>
    /// substitution at playback. Empty list means upload-only.
    ///
    /// JSON is always written as <c>postSteps</c>. Legacy files using the old
    /// <c>postDeliveryVerifications</c> key deserialise via <see cref="PostDeliveryVerificationsCompat"/>
    /// below — so existing test sets load unchanged.
    /// </summary>
    public List<VerificationStep> PostSteps { get; set; } = [];

    /// <summary>
    /// Read-on-deserialize back-compat for test sets saved before the Slice 2
    /// rename. When JSON carries <c>postDeliveryVerifications</c>, System.Text.Json
    /// populates this setter, which promotes the value into <see cref="PostSteps"/>.
    /// Never serialised back out (getter returns null).
    /// </summary>
    [JsonPropertyName("postDeliveryVerifications")]
    public List<VerificationStep>? PostDeliveryVerificationsCompat
    {
        get => null;
        set
        {
            if (value is not null && PostSteps.Count == 0)
                PostSteps = value;
        }
    }

    public AseXmlDeliveryTestCase ToTestCase(string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? Description : name,
        Description = Description,
        TemplateId = TemplateId,
        TransactionType = TransactionType,
        FieldValues = FieldValues,
        EndpointCode = EndpointCode,
        ValidateAgainstSchema = ValidateAgainstSchema,
        PostSteps = PostSteps
    };

    public static AseXmlDeliveryTestDefinition FromTestCase(AseXmlDeliveryTestCase tc) => new()
    {
        Description = tc.Description,
        TemplateId = tc.TemplateId,
        TransactionType = tc.TransactionType,
        FieldValues = tc.FieldValues,
        EndpointCode = tc.EndpointCode,
        ValidateAgainstSchema = tc.ValidateAgainstSchema,
        PostSteps = tc.PostSteps
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
    public List<VerificationStep> PostSteps { get; set; } = [];
}
