using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.AseXmlAgent.Templates;

/// <summary>
/// Metadata for an aseXML template. Lives alongside the template body as
/// {templateId}.manifest.json. Describes which fields are auto-generated,
/// user-supplied, or constant.
/// </summary>
public class TemplateManifest
{
    /// <summary>Stable identifier — must match the template filename (without .xml).</summary>
    public string TemplateId { get; set; } = "";

    /// <summary>The aseXML transaction root element, e.g. MeterFaultAndIssueNotification.</summary>
    public string TransactionType { get; set; } = "";

    /// <summary>Optional transaction group header (OWNX, DIGI, etc.) — for documentation only.</summary>
    public string TransactionGroup { get; set; } = "";

    /// <summary>Human-readable description shown to the LLM and in the UI.</summary>
    public string Description { get; set; } = "";

    /// <summary>Map of field token name → field spec. Keys must match {{tokens}} in the template body.</summary>
    public Dictionary<string, FieldSpec> Fields { get; set; } = [];

    /// <summary>Relative or absolute path to the template body. Set by the loader, not the JSON.</summary>
    [JsonIgnore]
    public string BodyPath { get; set; } = "";
}

/// <summary>
/// Per-field specification inside a template manifest.
/// </summary>
public class FieldSpec
{
    /// <summary>"auto" | "user" | "const".</summary>
    public string Source { get; set; } = "user";

    // ── auto ──────────────────────────────────────────────────────────────
    /// <summary>Generator key (messageId, transactionId, nowOffset, today). Only for source="auto".</summary>
    public string? Generator { get; set; }

    /// <summary>Pattern for id generators — may include the literal token "{rand8}".</summary>
    public string? Pattern { get; set; }

    /// <summary>Timezone offset for timestamp generators, e.g. "+10:00".</summary>
    public string? Offset { get; set; }

    // ── user ──────────────────────────────────────────────────────────────
    /// <summary>When true, render fails if no value is supplied for this field.</summary>
    public bool Required { get; set; }

    /// <summary>Example value shown to the LLM and used as a placeholder in the UI.</summary>
    public string? Example { get; set; }

    /// <summary>
    /// Optional per-field guidance surfaced to the LLM via the template catalogue.
    /// Use this for fields whose structure is non-obvious (e.g. a multi-line CSV
    /// payload with its own grammar) so the LLM has enough context to fill them.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional format tag that drives post-render validation. When set, the renderer
    /// invokes a format-specific validator on the resolved value before returning.
    /// Known values: "nem12" (NEM12 CSV body grammar check).
    /// </summary>
    public string? Format { get; set; }

    // ── const ─────────────────────────────────────────────────────────────
    /// <summary>Hardwired value for source="const".</summary>
    public string? Value { get; set; }
}
