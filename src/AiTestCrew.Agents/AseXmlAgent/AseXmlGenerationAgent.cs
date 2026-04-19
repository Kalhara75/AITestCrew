using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.AseXmlAgent.Templates;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.AseXmlAgent;

/// <summary>
/// Generates AEMO aseXML transaction payloads from templates.
///
/// Flow:
/// 1. Read preloaded test cases (reuse mode) OR ask the LLM to pick a template
///    and extract user-supplied field values from the objective.
/// 2. Render each case deterministically via <see cref="AseXmlRenderer"/> —
///    LLM never writes XML; it only fills in values.
/// 3. Write rendered XML to a run-scoped folder under <see cref="AseXmlConfig.OutputPath"/>.
/// 4. Emit one TestStep per rendered file, attach generated IDs to Metadata,
///    and persist the test cases back via Metadata["generatedTestCases"].
/// </summary>
public class AseXmlGenerationAgent : BaseTestAgent
{
    private readonly TestEnvironmentConfig _config;
    private readonly TemplateRegistry _templates;

    public override string Name => "aseXML Generation Agent";
    public override string Role => "Senior AEMO B2B Test Engineer";

    public AseXmlGenerationAgent(
        Kernel kernel,
        ILogger<AseXmlGenerationAgent> logger,
        TestEnvironmentConfig config,
        TemplateRegistry templates) : base(kernel, logger)
    {
        _config = config;
        _templates = templates;
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.AseXml_Generate);

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();

        Logger.LogInformation("[{Agent}] Starting task: {Desc}", Name, task.Description);

        try
        {
            // ── Reuse mode: use preloaded test cases, skip LLM ──
            var envParams = StepParameterSubstituter.ReadEnvironmentParameters(task.Parameters);
            List<AseXmlTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
                && preloaded is List<AseXmlTestCase> saved)
            {
                testCases = envParams.Count > 0
                    ? saved.Select(tc => StepParameterSubstituter.Apply(tc, envParams)).ToList()
                    : saved;
                steps.Add(TestStep.Pass("load-cases",
                    $"Loaded {testCases.Count} saved aseXML test case(s) (reuse mode — skipping LLM generation)"));
            }

            if (testCases is null)
            {
                if (_templates.All().Count == 0)
                {
                    steps.Add(TestStep.Err("catalogue",
                        $"No aseXML templates are loaded. Place template+manifest pairs under '{_templates.TemplatesPath}'."));
                    return BuildResult(task, steps, TestStatus.Error,
                        "No aseXML templates available.", sw, new List<AseXmlTestDefinition>());
                }

                testCases = await GenerateTestCasesAsync(task, ct);
                if (testCases is null || testCases.Count == 0)
                {
                    steps.Add(TestStep.Err("generate-cases",
                        "LLM did not return any aseXML test cases for this objective."));
                    return BuildResult(task, steps, TestStatus.Error,
                        "LLM failed to produce aseXML test cases.", sw, new List<AseXmlTestDefinition>());
                }

                steps.Add(TestStep.Pass("generate-cases",
                    $"LLM produced {testCases.Count} aseXML test case(s)"));
            }

            // ── Render each case ──
            var runOutputDir = ResolveRunOutputDir(task.Id);
            Directory.CreateDirectory(runOutputDir);

            var caseIndex = 0;
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                caseIndex++;
                steps.Add(RenderOne(tc, caseIndex, runOutputDir));
            }

            var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
            var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
            var status = hasErrors ? TestStatus.Error
                       : hasFails ? TestStatus.Failed
                       : TestStatus.Passed;

            var summary = status == TestStatus.Passed
                ? $"Rendered {testCases.Count} aseXML payload(s) to {runOutputDir}"
                : $"Rendered {testCases.Count(_ => true)} aseXML case(s) with issues; see step detail.";

            var definitions = testCases.Select(AseXmlTestDefinition.FromTestCase).ToList();
            return BuildResult(task, steps, status, summary, sw, definitions, runOutputDir);
        }
        catch (OperationCanceledException)
        {
            return BuildResult(task, steps, TestStatus.Error,
                "Test execution was cancelled", sw, new List<AseXmlTestDefinition>());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] Unhandled error", Name);
            steps.Add(TestStep.Err("fatal", $"Unhandled exception: {ex.Message}"));
            return BuildResult(task, steps, TestStatus.Error,
                $"Agent error: {ex.Message}", sw, new List<AseXmlTestDefinition>());
        }
    }

    // ─────────────────────────────────────────────────────
    // Core
    // ─────────────────────────────────────────────────────

    private TestStep RenderOne(AseXmlTestCase tc, int index, string runOutputDir)
    {
        var stepSw = Stopwatch.StartNew();
        var action = $"render[{index}] {tc.TemplateId}";

        var template = _templates.Get(tc.TemplateId);
        if (template is null)
        {
            var known = string.Join(", ", _templates.All().Select(t => t.Manifest.TemplateId));
            return TestStep.Fail(action,
                $"Template '{tc.TemplateId}' not found. Known templates: {known}");
        }

        try
        {
            var result = AseXmlRenderer.Render(template.Manifest, template.Body, tc.FieldValues);

            var safeName = MakeFileSafe(string.IsNullOrWhiteSpace(tc.Name) ? tc.TemplateId : tc.Name);
            var outputPath = Path.Combine(runOutputDir, $"{index:00}-{safeName}.xml");
            File.WriteAllText(outputPath, result.Xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var messageId = result.ResolvedFields.TryGetValue("MessageID", out var mid) ? mid : "";
            var transactionId = result.ResolvedFields.TryGetValue("TransactionID", out var tid) ? tid : "";

            var detail = new StringBuilder();
            detail.AppendLine($"Template: {tc.TemplateId} ({template.Manifest.TransactionType})");
            detail.AppendLine($"Output:   {outputPath}");
            if (!string.IsNullOrEmpty(messageId)) detail.AppendLine($"MessageID: {messageId}");
            if (!string.IsNullOrEmpty(transactionId)) detail.AppendLine($"TransactionID: {transactionId}");
            detail.AppendLine();
            detail.AppendLine("Resolved fields:");
            foreach (var (k, v) in result.ResolvedFields.OrderBy(kv => kv.Key))
                detail.AppendLine($"  {k} = {v}");
            detail.AppendLine();
            var preview = result.Xml.Length > 1200 ? result.Xml[..1200] + "\n…[truncated]" : result.Xml;
            detail.AppendLine("XML preview:");
            detail.Append(preview);

            return new TestStep
            {
                Action = action,
                Summary = $"Rendered '{tc.TemplateId}' → {Path.GetFileName(outputPath)}"
                    + (string.IsNullOrEmpty(messageId) ? "" : $" (MessageID {messageId})"),
                Status = TestStatus.Passed,
                Detail = detail.ToString(),
                Duration = stepSw.Elapsed
            };
        }
        catch (AseXmlRenderException ex)
        {
            return TestStep.Fail(action, ex.Message);
        }
        catch (Exception ex)
        {
            return TestStep.Err(action, $"Unexpected render failure: {ex.Message}");
        }
    }

    private async Task<List<AseXmlTestCase>?> GenerateTestCasesAsync(TestTask task, CancellationToken ct)
    {
        var catalogue = BuildCatalogueForLlm();

        var prompt = $$"""
            You are generating aseXML transaction test cases for an AEMO B2B test run.

            Objective:
            "{{task.Description}}"

            Available templates (pick the ONE that best matches the objective; copy its
            templateId and transactionType verbatim):
            {{catalogue}}

            RULES:
            - Read the objective LITERALLY. Return exactly one test case unless the
              objective explicitly asks for multiple variants (e.g. "happy path AND reject case").
            - Populate only fields listed as "source: user" in the chosen template.
            - NEVER set auto fields (MessageID, TransactionID, MessageDate, TransactionDate, etc.) —
              those are generated by the system at render time.
            - NEVER set const fields — those are hardwired in the template.
            - If the objective does not provide a required user field, leave it blank — a
              deterministic validation step will surface the gap.
            - Keep the description short and specific (mentions the key value under test,
              e.g. "MFN One In All In for NMI 4103035611").

            Respond ONLY with a JSON array of this shape (no prose, no markdown fences):
            [
              {
                "name": "short label",
                "description": "what this case verifies",
                "templateId": "MFN-OneInAllIn",
                "transactionType": "MeterFaultAndIssueNotification",
                "fieldValues": {
                  "NMI": "4103035611"
                },
                "validateAgainstSchema": false
              }
            ]
            """;

        return await AskLlmForJsonAsync<List<AseXmlTestCase>>(prompt, ct);
    }

    private string BuildCatalogueForLlm()
    {
        var sb = new StringBuilder();
        foreach (var t in _templates.All())
        {
            sb.AppendLine($"- templateId: {t.Manifest.TemplateId}");
            sb.AppendLine($"  transactionType: {t.Manifest.TransactionType}");
            if (!string.IsNullOrWhiteSpace(t.Manifest.TransactionGroup))
                sb.AppendLine($"  transactionGroup: {t.Manifest.TransactionGroup}");
            if (!string.IsNullOrWhiteSpace(t.Manifest.Description))
                sb.AppendLine($"  description: {t.Manifest.Description}");

            var userFields = t.Manifest.Fields
                .Where(f => string.Equals(f.Value.Source, "user", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (userFields.Count > 0)
            {
                sb.AppendLine("  userFields:");
                foreach (var (name, spec) in userFields)
                {
                    var req = spec.Required ? "required" : "optional";
                    var ex  = string.IsNullOrWhiteSpace(spec.Example) ? "" : $" example=\"{spec.Example}\"";
                    sb.AppendLine($"    - {name} ({req}){ex}");
                    if (!string.IsNullOrWhiteSpace(spec.Description))
                        sb.AppendLine($"      description: {spec.Description}");
                    if (!string.IsNullOrWhiteSpace(spec.Format))
                        sb.AppendLine($"      format: {spec.Format}");
                }
            }
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────
    // Utilities
    // ─────────────────────────────────────────────────────

    private string ResolveRunOutputDir(string runId)
    {
        var root = Path.IsPathRooted(_config.AseXml.OutputPath)
            ? _config.AseXml.OutputPath
            : Path.Combine(AppContext.BaseDirectory, _config.AseXml.OutputPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(root, $"{stamp}_{runId}");
    }

    private static string MakeFileSafe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.Trim().Replace(' ', '-');
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private TestResult BuildResult(
        TestTask task, List<TestStep> steps, TestStatus status, string summary,
        Stopwatch sw, List<AseXmlTestDefinition> definitions, string? outputDir = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["generatedTestCases"] = definitions
        };
        if (outputDir is not null) metadata["outputDir"] = outputDir;

        return new TestResult
        {
            ObjectiveId = task.Id,
            ObjectiveName = task.Description,
            AgentName = Name,
            Status = status,
            Summary = summary,
            Steps = steps,
            Duration = sw.Elapsed,
            Metadata = metadata
        };
    }
}
