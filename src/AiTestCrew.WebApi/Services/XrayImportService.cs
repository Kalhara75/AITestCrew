using AiTestCrew.Core.Configuration;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Capabilities;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.WebApi.Integrations.JiraXray;

namespace AiTestCrew.WebApi.Services;

public interface IXrayImportService
{
    Task<XrayImportPreview> PreviewAsync(XrayImportRequest request, CancellationToken ct = default);
    Task<XrayImportResult> ConfirmAsync(XrayImportConfirmRequest request, CancellationToken ct = default);
}

public class XrayImportService : IXrayImportService
{
    private readonly IJiraXrayClient _xrayClient;
    private readonly IChatCompletionService _chat;
    private readonly ITestSetRepository _testSetRepo;
    private readonly GapRequirementWriter _gapWriter;
    private readonly ILogger<XrayImportService> _logger;

    public XrayImportService(
        IJiraXrayClient xrayClient,
        IChatCompletionService chat,
        ITestSetRepository testSetRepo,
        GapRequirementWriter gapWriter,
        ILogger<XrayImportService> logger)
    {
        _xrayClient = xrayClient;
        _chat = chat;
        _testSetRepo = testSetRepo;
        _gapWriter = gapWriter;
        _logger = logger;
    }

    public async Task<XrayImportPreview> PreviewAsync(XrayImportRequest request, CancellationToken ct = default)
    {
        var ticket = await _xrayClient.GetTestAsync(request.TicketKey, ct);
        var proposed = await DecomposeAsync(ticket, ct);
        foreach (var obj in proposed)
            obj.MappingRows = await MapFragmentsAsync(ticket, obj, ct);

        var gapTitles = proposed
            .SelectMany(o => o.MappingRows)
            .Where(r => r.Kind == "unsupported" && r.SuggestedReqTitle != null)
            .Select(r => r.SuggestedReqTitle!)
            .Distinct()
            .ToList();

        return new XrayImportPreview
        {
            TicketKey = request.TicketKey,
            TicketSummary = ticket.Summary,
            ModuleId = request.ModuleId,
            TestSetId = request.TestSetId,
            ProposedObjectives = proposed,
            ReviewCarefullyFlag = proposed.Count > 4,
            DraftGapReqTitles = gapTitles
        };
    }

    public async Task<XrayImportResult> ConfirmAsync(XrayImportConfirmRequest request, CancellationToken ct = default)
    {
        var preview = request.Preview;
        var objectives = FilterAndMerge(preview.ProposedObjectives, request);

        var testSet = await _testSetRepo.LoadAsync(preview.ModuleId, preview.TestSetId)
                      ?? throw new InvalidOperationException(
                             "Test set not found: " + preview.ModuleId + "/" + preview.TestSetId);
        var persistedIds = new List<string>();
        var gapPaths = new List<string>();
        var placeholders = new List<string>();

        foreach (var obj in objectives)
        {
            if (request.TitleOverrides.TryGetValue(obj.Slug, out var overrideTitle))
                obj.Title = overrideTitle;

            foreach (var row in obj.MappingRows.Where(r => r.Kind == "unsupported" && r.SuggestedReqTitle != null))
            {
                try
                {
                    var spec = new GapReqSpec
                    {
                        SuggestedTitle = row.SuggestedReqTitle!,
                        TicketKey = preview.TicketKey,
                        TicketSummary = preview.TicketSummary,
                        StepText = row.SourceFragment,
                        Rationale = row.Rationale,
                        SuggestedExtensionPoint = row.SuggestedExtensionPoint ?? "To be determined.",
                        Area = "tooling"
                    };
                    var path = _gapWriter.Write(spec);
                    gapPaths.Add(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed writing gap REQ: {Fragment}", row.SourceFragment);
                }
            }

            placeholders.AddRange(
                obj.MappingRows
                   .Where(r => r.Kind == "placeholder")
                   .Select(r => r.SourceFragment));

            var existing = testSet.TestObjectives.FirstOrDefault(o =>
                o.XrayTicketKey == preview.TicketKey && o.XrayObjectiveSlug == obj.Slug);

            TestObjective testObj;
            if (existing != null)
            {
                testObj = existing;
                testObj.Name = obj.Title;
                testObj.Preconditions = obj.Preconditions;
                testObj.TestDataNotes = obj.TestDataNotes;
            }
            else
            {
                testObj = new TestObjective
                {
                    Id = SlugHelper.ToSlug(obj.Title),
                    Name = obj.Title,
                    ParentObjective = preview.TicketSummary,
                    XrayTicketKey = preview.TicketKey,
                    XrayObjectiveSlug = obj.Slug,
                    Preconditions = obj.Preconditions,
                    TestDataNotes = obj.TestDataNotes,
                    Source = "ImportedFromXray",
                    AgentName = ""
                };
                testSet.TestObjectives.Add(testObj);
            }

            // Clear existing imported steps on re-import so mapping is idempotent.
            testObj.ApiSteps.Clear();
            testObj.WebUiSteps.Clear();
            testObj.DesktopUiSteps.Clear();
            testObj.AseXmlSteps.Clear();
            testObj.AseXmlDeliverySteps.Clear();
            MapRowsToObjective(obj.MappingRows, testObj);
            persistedIds.Add(testObj.Id);
        }

        await _testSetRepo.SaveAsync(testSet, preview.ModuleId);

        return new XrayImportResult
        {
            PersistedObjectiveIds = persistedIds,
            GapReqPaths = gapPaths,
            PlaceholderStepDescriptions = placeholders
        };
    }
    private async Task<List<ProposedObjective>> DecomposeAsync(XrayTestDto ticket, CancellationToken ct)
    {
        // For description-driven tests (Steps empty), decompose using Expected Outcome bullets.
        // For step-structured tests, use the structured Steps array.
        var isDescriptionDriven = ticket.Steps.Count == 0;
        var allSteps = isDescriptionDriven
            ? string.Join(
                "\n",
                (ticket.ParsedDescription?.ExpectedOutcomes ?? []).Select((eo, i) =>
                    string.Format("Expected Outcome {0}: {1}", i + 1, eo)))
            : string.Join(
                "\n",
                ticket.Steps.Select((s, i) =>
                    string.Format("Step {0}: Action=\"{1}\" ExpectedResult=\"{2}\"",
                        i + 1, s.Action, s.ExpectedResult)));

        var prompt =
            "You are an expert test architect. A Jira Xray test ticket has been fetched.\n" +
            "Decide how many AITestCrew TestObjectives the ticket should decompose into.\n" +
            "Rules:\n" +
            "  - Use 1 objective if all steps test ONE coherent user journey.\n" +
            "  - Split ONLY for different actors, states, or independent negative tests.\n" +
            "  - Prefer 1-2 objectives.\n" +
            "  - Never split by step number alone.\n\n" +
            "Ticket: " + ticket.Key + " -- " + ticket.Summary + "\n" +
            "Preconditions: " + string.Join("; ", ticket.ParsedDescription?.Preconditions ?? []) + "\n" +
            "Steps:\n" + allSteps + "\n\n" +
            "Return a JSON array where each element has:\n" +
            "  slug (string, kebab-case, unique per ticket),\n" +
            "  title (string, human-readable),\n" +
            "  rationale (string, one sentence),\n" +
            "  assignedFragments (string[]),\n" +
            "  preconditions (string[]),\n" +
            "  testDataNotes (string or null)\n" +
            "JSON only -- no commentary.";

        var history = new ChatHistory();
        history.AddUserMessage(prompt);
        var result = await _chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        var json = CleanJson(result.Content ?? "[]");

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var proposed = JsonSerializer.Deserialize<List<ProposedObjective>>(json, opts) ?? [];
            if (proposed.Count == 0)
                proposed.Add(DefaultObjective(ticket));
            return proposed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decomposition LLM response not valid JSON; using single objective.");
            return [DefaultObjective(ticket)];
        }
    }

    private static ProposedObjective DefaultObjective(XrayTestDto ticket)
    {
        var fragments = ticket.Steps.Count > 0
            ? ticket.Steps.Select(s => s.Action).ToList()
            : ticket.ParsedDescription?.ExpectedOutcomes ?? [];
        return new ProposedObjective
        {
            Slug = SlugHelper.ToSlug(ticket.Summary),
            Title = ticket.Summary,
            Rationale = "Single objective (LLM response fallback).",
            AssignedFragments = fragments,
            Preconditions = ticket.ParsedDescription?.Preconditions ?? [],
            TestDataNotes = ticket.ParsedDescription?.TestData
        };
    }
    private async Task<List<XrayMappingRow>> MapFragmentsAsync(
        XrayTestDto ticket, ProposedObjective obj, CancellationToken ct)
    {
        var capMd = CapabilityRegistry.GetMarkdown();
        var fragments = string.Join(
            "\n",
            obj.AssignedFragments.Select((f, i) =>
                string.Format("[{0}] {1}", i + 1, f)));

        var prompt =
            "You are mapping Xray test steps to AITestCrew step types.\n\n" +
            "AITestCrew capabilities:\n" + capMd + "\n\n" +
            "Objective: " + obj.Title + "\n" +
            "Fragments to map:\n" + fragments + "\n\n" +
            "For each fragment return a JSON object with:\n" +
            "  sourceFragment (string),\n" +
            "  kind (one of: api | webUi | desktopUi | asexml | asexmlDelivery | postStep | placeholder | unsupported),\n" +
            "  target (string or null),\n" +
            "  postStepType (string or null),\n" +
            "  confidence (float 0-1),\n" +
            "  rationale (string),\n" +
            "  suggestedReqTitle (string or null -- only when kind=unsupported),\n" +
            "  suggestedExtensionPoint (string or null -- only when kind=unsupported)\n" +
            "Use placeholder for vague steps. Use unsupported for missing capabilities.\n" +
            "Return a JSON array only.";

        var history = new ChatHistory();
        history.AddUserMessage(prompt);
        var result = await _chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        var json = CleanJson(result.Content ?? "[]");

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<XrayMappingRow>>(json, opts) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mapping LLM response not valid JSON for {Slug}.", obj.Slug);
            return obj.AssignedFragments
                .Select(f => new XrayMappingRow
                {
                    SourceFragment = f,
                    Kind = "placeholder",
                    Confidence = 0,
                    Rationale = "LLM mapping failed; treating as placeholder."
                })
                .ToList();
        }
    }
    private static void MapRowsToObjective(List<XrayMappingRow> rows, TestObjective obj)
    {
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case "webUi":
                case "placeholder":
                    var webDef = new WebUiTestDefinition
                    {
                        Description = row.SourceFragment,
                        StartUrl = "",
                        Steps = []
                    };
                    obj.WebUiSteps.Add(webDef);
                    if (string.IsNullOrEmpty(obj.TargetType) || obj.TargetType == "API_REST")
                    {
                        obj.TargetType = row.Target ?? "UI_Web_Blazor";
                        obj.AgentName = "";
                    }
                    break;
                case "api":
                    obj.TargetType = row.Target ?? "API_REST";
                    break;
                case "desktopUi":
                    obj.TargetType = row.Target ?? "UI_Desktop_WinForms";
                    break;
                case "asexml":
                    obj.TargetType = row.Target ?? "AseXml_Generate";
                    break;
                case "asexmlDelivery":
                    obj.TargetType = row.Target ?? "AseXml_Deliver";
                    break;
                default:
                    break;
            }
        }
        if (string.IsNullOrEmpty(obj.TargetType))
            obj.TargetType = "API_REST";
    }

    private static List<ProposedObjective> FilterAndMerge(
        List<ProposedObjective> all, XrayImportConfirmRequest request)
    {
        var accepted = request.AcceptedObjectiveSlugs.Count > 0
            ? all.Where(o => request.AcceptedObjectiveSlugs.Contains(o.Slug)).ToList()
            : all.ToList();

        foreach (var merge in request.MergeRequests)
        {
            var src = accepted.FirstOrDefault(o => o.Slug == merge.SlugToMerge);
            var dst = accepted.FirstOrDefault(o => o.Slug == merge.MergeIntoSlug);
            if (src == null || dst == null) continue;
            dst.AssignedFragments.AddRange(src.AssignedFragments);
            dst.MappingRows.AddRange(src.MappingRows);
            dst.Preconditions.AddRange(src.Preconditions.Where(p => !dst.Preconditions.Contains(p)));
            accepted.Remove(src);
        }

        if (request.CollapseToSingle && accepted.Count > 1)
        {
            var first = accepted[0];
            for (int i = 1; i < accepted.Count; i++)
            {
                first.AssignedFragments.AddRange(accepted[i].AssignedFragments);
                first.MappingRows.AddRange(accepted[i].MappingRows);
            }
            accepted = [first];
        }
        return accepted;
    }

    private static string CleanJson(string raw)
    {
        var s = raw.Trim();
        var fence = new string((char)96, 3);
        if (s.StartsWith(fence))
        {
            int newline = s.IndexOf((char)10);
            if (newline >= 0)
                s = s[(newline + 1)..];
            int last = s.LastIndexOf(fence);
            if (last >= 0)
                s = s[..last];
        }
        return s.Trim();
    }
}
