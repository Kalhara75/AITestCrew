namespace AiTestCrew.Agents.AseXmlAgent;

/// <summary>
/// The definition of an aseXML test step — template + user-supplied field values.
/// Stored inside <see cref="AiTestCrew.Agents.Persistence.TestObjective.AseXmlSteps"/>.
///
/// Rendered XML and auto-generated IDs/timestamps are NOT persisted — they are
/// regenerated on every run so that re-executing a saved test produces a fresh
/// MessageID and TransactionID, matching real-world transaction behaviour.
/// </summary>
public class AseXmlTestDefinition
{
    /// <summary>Free-form description shown in logs and the UI (e.g. "MFN for NMI 4103035611").</summary>
    public string Description { get; set; } = "";

    /// <summary>The templateId from the manifest (e.g. "MFN-OneInAllIn"). Required.</summary>
    public string TemplateId { get; set; } = "";

    /// <summary>Transaction type, copied from the manifest for display/filtering. Informational.</summary>
    public string TransactionType { get; set; } = "";

    /// <summary>User-supplied values for manifest fields with source="user".</summary>
    public Dictionary<string, string> FieldValues { get; set; } = [];

    /// <summary>
    /// Forward-compatible flag for XSD validation. Defaults to false; the validator
    /// itself is not shipped in Phase 1 and will arrive alongside a checked-in XSD.
    /// </summary>
    public bool ValidateAgainstSchema { get; set; }

    /// <summary>Maps this definition to its runtime-mutable test case.</summary>
    public AseXmlTestCase ToTestCase(string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? Description : name,
        Description = Description,
        TemplateId = TemplateId,
        TransactionType = TransactionType,
        FieldValues = FieldValues,
        ValidateAgainstSchema = ValidateAgainstSchema
    };

    /// <summary>Builds a definition (for persistence) from a runtime test case.</summary>
    public static AseXmlTestDefinition FromTestCase(AseXmlTestCase tc) => new()
    {
        Description = tc.Description,
        TemplateId = tc.TemplateId,
        TransactionType = tc.TransactionType,
        FieldValues = tc.FieldValues,
        ValidateAgainstSchema = tc.ValidateAgainstSchema
    };
}

/// <summary>
/// Runtime-mutable representation of an aseXML test case used by the agent.
/// Mirrors the (definition, test-case) split used for API + UI agents.
/// </summary>
public class AseXmlTestCase
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TransactionType { get; set; } = "";
    public Dictionary<string, string> FieldValues { get; set; } = [];
    public bool ValidateAgainstSchema { get; set; }
}
