namespace AiTestCrew.WebApi.Services;

public class XrayImportRequest
{
    public string TicketKey { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string TestSetId { get; set; } = "";
}

public class XrayMappingRow
{
    public string SourceFragment { get; set; } = "";
    /// <summary>api | webUi | desktopUi | asexml | asexmlDelivery | postStep | placeholder | unsupported</summary>
    public string Kind { get; set; } = "";
    public string? Target { get; set; }
    public string? PostStepType { get; set; }
    public double Confidence { get; set; }
    public string Rationale { get; set; } = "";
    public string? SuggestedReqTitle { get; set; }
    public string? SuggestedExtensionPoint { get; set; }
    public object? Definition { get; set; }
}

public class ProposedObjective
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Rationale { get; set; } = "";
    public List<string> AssignedFragments { get; set; } = [];
    public List<XrayMappingRow> MappingRows { get; set; } = [];
    public List<string> Preconditions { get; set; } = [];
    public string? TestDataNotes { get; set; }
}

public class XrayImportPreview
{
    public string TicketKey { get; set; } = "";
    public string TicketSummary { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string TestSetId { get; set; } = "";
    public List<ProposedObjective> ProposedObjectives { get; set; } = [];
    /// <summary>True when more than 4 objectives proposed -- QA should review carefully.</summary>
    public bool ReviewCarefullyFlag { get; set; }
    public List<string> DraftGapReqTitles { get; set; } = [];
}

public class XrayImportConfirmRequest
{
    public XrayImportPreview Preview { get; set; } = new();
    /// <summary>Slugs of objectives to persist. Empty list = accept all.</summary>
    public List<string> AcceptedObjectiveSlugs { get; set; } = [];
    /// <summary>When true, all accepted objectives are collapsed into a single one.</summary>
    public bool CollapseToSingle { get; set; } = false;
    /// <summary>Per-slug title overrides the QA edited in the dialog.</summary>
    public Dictionary<string, string> TitleOverrides { get; set; } = new();
    /// <summary>Each pair (slugToMerge, mergeIntoSlug) merges two proposed objectives.</summary>
    public List<MergeRequest> MergeRequests { get; set; } = [];
}

public class MergeRequest
{
    public string SlugToMerge { get; set; } = "";
    public string MergeIntoSlug { get; set; } = "";
}

public class XrayImportResult
{
    public List<string> PersistedObjectiveIds { get; set; } = [];
    public List<string> GapReqPaths { get; set; } = [];
    public List<string> PlaceholderStepDescriptions { get; set; } = [];
}

public class GapReqSpec
{
    public string SuggestedTitle { get; set; } = "";
    public string TicketKey { get; set; } = "";
    public string TicketSummary { get; set; } = "";
    public string StepText { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string SuggestedExtensionPoint { get; set; } = "";
    public string Area { get; set; } = "tooling";
}