using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.AseXmlAgent.Templates;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Core.Utilities;

namespace AiTestCrew.Agents.AseXmlAgent;

/// <summary>
/// Renders an aseXML payload and uploads it to a Bravo inbound drop location
/// resolved from <c>mil.V2_MIL_EndPoint</c> by <c>EndPointCode</c>.
///
/// Self-contained: does not depend on a preceding Generate task. On reuse,
/// the rendered payload is produced fresh (new MessageID / TransactionID /
/// timestamps) and uploaded to the same endpoint.
///
/// Phase 3: after a successful upload, the agent optionally runs UI verification
/// steps (Legacy MVC, Blazor, or WinForms) attached to the delivery test case.
/// Values from the render (NMI, MessageID, TransactionID, filename, any template
/// field) are injected into every UI step field via <c>{{Token}}</c> substitution
/// at playback. Siblings are discovered via <see cref="CanHandleAsync"/> dispatch.
/// </summary>
public class AseXmlDeliveryAgent : BaseTestAgent
{
    private readonly TestEnvironmentConfig _config;
    private readonly TemplateRegistry _templates;
    private readonly IEndpointResolver _endpoints;
    private readonly DropTargetFactory _dropFactory;
    private readonly IReadOnlyList<ITestAgent> _siblings;

    public override string Name => "aseXML Delivery Agent";
    public override string Role => "Senior AEMO B2B Test Engineer";

    public AseXmlDeliveryAgent(
        Kernel kernel,
        ILogger<AseXmlDeliveryAgent> logger,
        TestEnvironmentConfig config,
        TemplateRegistry templates,
        IEndpointResolver endpoints,
        DropTargetFactory dropFactory,
        IEnumerable<ITestAgent> siblings) : base(kernel, logger)
    {
        _config = config;
        _templates = templates;
        _endpoints = endpoints;
        _dropFactory = dropFactory;
        // Filter self out at assignment time so we can't recurse into ourselves.
        _siblings = siblings.Where(a => a is not AseXmlDeliveryAgent).ToList();
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.AseXml_Deliver);

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();
        var deliveries = new List<Dictionary<string, object?>>();

        Logger.LogInformation("[{Agent}] Starting task: {Desc}", Name, task.Description);

        try
        {
            // ── Load test cases ────────────────────────────────────────────
            List<AseXmlDeliveryTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
                && preloaded is List<AseXmlDeliveryTestCase> saved)
            {
                testCases = saved;
                steps.Add(TestStep.Pass("load-cases",
                    $"Loaded {testCases.Count} saved delivery case(s) (reuse mode — skipping LLM generation)"));
            }

            if (testCases is null)
            {
                if (_templates.All().Count == 0)
                {
                    steps.Add(TestStep.Err("catalogue",
                        $"No aseXML templates loaded. Place template+manifest pairs under '{_templates.TemplatesPath}'."));
                    return BuildResult(task, steps, TestStatus.Error,
                        "No aseXML templates available.", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
                }

                testCases = await GenerateTestCasesAsync(task, ct);
                if (testCases is null || testCases.Count == 0)
                {
                    steps.Add(TestStep.Err("generate-cases",
                        "LLM did not return any aseXML delivery cases for this objective."));
                    return BuildResult(task, steps, TestStatus.Error,
                        "LLM failed to produce delivery test cases.", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
                }

                steps.Add(TestStep.Pass("generate-cases",
                    $"LLM produced {testCases.Count} delivery case(s)"));
            }

            // ── Apply CLI / test-set-level endpoint override ──────────────
            var endpointOverride = task.Parameters.TryGetValue("EndpointCode", out var ec)
                ? ec as string : null;
            if (!string.IsNullOrWhiteSpace(endpointOverride))
            {
                foreach (var tc in testCases)
                {
                    if (string.IsNullOrWhiteSpace(tc.EndpointCode))
                        tc.EndpointCode = endpointOverride!;
                }
            }

            // ── Resolve run output dir (for local debug copies) ───────────
            var runOutputDir = ResolveRunOutputDir(task.Id);
            Directory.CreateDirectory(runOutputDir);

            // ── For each case: render → resolve → [package] → upload ──────
            var caseIndex = 0;
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                caseIndex++;
                await DeliverOneAsync(tc, caseIndex, runOutputDir, steps, deliveries, ct);
            }

            var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
            var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
            var status = hasErrors ? TestStatus.Error
                       : hasFails ? TestStatus.Failed
                       : TestStatus.Passed;

            var summary = status == TestStatus.Passed
                ? $"Delivered {testCases.Count} aseXML payload(s). Debug copies at {runOutputDir}."
                : $"Delivery attempted for {testCases.Count} case(s) with issues; see step detail.";

            var definitions = testCases.Select(AseXmlDeliveryTestDefinition.FromTestCase).ToList();
            return BuildResult(task, steps, status, summary, sw, definitions, deliveries, runOutputDir);
        }
        catch (OperationCanceledException)
        {
            return BuildResult(task, steps, TestStatus.Error,
                "Test execution was cancelled", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] Unhandled error", Name);
            steps.Add(TestStep.Err("fatal", $"Unhandled exception: {ex.Message}"));
            return BuildResult(task, steps, TestStatus.Error,
                $"Agent error: {ex.Message}", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
        }
    }

    // ─────────────────────────────────────────────────────
    // Core delivery pipeline
    // ─────────────────────────────────────────────────────

    private async Task DeliverOneAsync(
        AseXmlDeliveryTestCase tc,
        int index,
        string runOutputDir,
        List<TestStep> steps,
        List<Dictionary<string, object?>> deliveries,
        CancellationToken ct)
    {
        // 1) Render
        var renderAction = $"render[{index}] {tc.TemplateId}";
        var template = _templates.Get(tc.TemplateId);
        if (template is null)
        {
            var known = string.Join(", ", _templates.All().Select(t => t.Manifest.TemplateId));
            steps.Add(TestStep.Fail(renderAction,
                $"Template '{tc.TemplateId}' not found. Known templates: {known}"));
            return;
        }

        string xml;
        Dictionary<string, string> resolvedFields;
        try
        {
            var result = AseXmlRenderer.Render(template.Manifest, template.Body, tc.FieldValues);
            xml = result.Xml;
            resolvedFields = result.ResolvedFields;
        }
        catch (AseXmlRenderException ex)
        {
            steps.Add(TestStep.Fail(renderAction, ex.Message));
            return;
        }

        var messageId = resolvedFields.TryGetValue("MessageID", out var mid) ? mid : "";
        var transactionId = resolvedFields.TryGetValue("TransactionID", out var tid) ? tid : "";
        var safeName = MakeFileSafe(string.IsNullOrWhiteSpace(tc.Name) ? tc.TemplateId : tc.Name);
        var xmlLocalPath = Path.Combine(runOutputDir, $"{index:00}-{safeName}.xml");
        File.WriteAllText(xmlLocalPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        steps.Add(new TestStep
        {
            Action = renderAction,
            Summary = $"Rendered '{tc.TemplateId}'"
                + (string.IsNullOrEmpty(messageId) ? "" : $" (MessageID {messageId})"),
            Status = TestStatus.Passed,
            Detail = BuildRenderDetail(tc, template.Manifest.TransactionType, xmlLocalPath, resolvedFields, xml),
        });

        // 2) Resolve endpoint
        var resolveAction = $"resolve-endpoint[{index}]";
        if (string.IsNullOrWhiteSpace(tc.EndpointCode))
        {
            steps.Add(TestStep.Fail(resolveAction,
                "No EndpointCode supplied — set it in the objective or pass --endpoint <code>."));
            return;
        }

        BravoEndpoint? endpoint;
        try
        {
            endpoint = await _endpoints.ResolveAsync(tc.EndpointCode, ct);
        }
        catch (InvalidOperationException ex)  // e.g. connection string not configured
        {
            steps.Add(TestStep.Err(resolveAction, ex.Message));
            return;
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(resolveAction,
                $"Failed to query mil.V2_MIL_EndPoint for '{tc.EndpointCode}': {ex.Message}"));
            return;
        }

        if (endpoint is null)
        {
            steps.Add(TestStep.Fail(resolveAction,
                $"EndPointCode '{tc.EndpointCode}' not found in mil.V2_MIL_EndPoint."));
            return;
        }

        steps.Add(TestStep.Pass(resolveAction,
            $"Endpoint '{endpoint.EndPointCode}' → host={endpoint.FtpServer}, outbox={endpoint.OutBoxUrl}, zipped={endpoint.IsOutboundFilesZipped}"));

        // 3) Determine remote file name + payload (zip if configured)
        var baseFileName = !string.IsNullOrEmpty(messageId) ? messageId : safeName;
        string remoteFileName;
        Stream uploadContent;
        long uploadBytes;
        string uploadedAs;

        if (endpoint.IsOutboundFilesZipped)
        {
            var packageAction = $"package[{index}]";
            MemoryStream zipStream;
            try
            {
                zipStream = XmlZipPackager.Package(xml, $"{baseFileName}.xml");
            }
            catch (Exception ex)
            {
                steps.Add(TestStep.Err(packageAction, $"Failed to build zip: {ex.Message}"));
                return;
            }

            var uncompressed = Encoding.UTF8.GetByteCount(xml);
            var compressed = zipStream.Length;
            var ratio = uncompressed == 0 ? 0 : (double)compressed / uncompressed;
            steps.Add(TestStep.Pass(packageAction,
                $"Packaged XML into zip — {compressed} bytes (uncompressed {uncompressed}, ratio {ratio:P0})"));

            // Write local zip copy for developer inspection.
            var zipLocalPath = Path.Combine(runOutputDir, $"{index:00}-{safeName}.zip");
            File.WriteAllBytes(zipLocalPath, zipStream.ToArray());

            zipStream.Position = 0;
            uploadContent = zipStream;
            uploadBytes = compressed;
            remoteFileName = $"{baseFileName}.zip";
            uploadedAs = "zip";
        }
        else
        {
            var xmlBytes = Encoding.UTF8.GetBytes(xml);
            uploadContent = new MemoryStream(xmlBytes);
            uploadBytes = xmlBytes.LongLength;
            remoteFileName = $"{baseFileName}.xml";
            uploadedAs = "xml";
        }

        // 4) Upload
        var uploadAction = $"upload[{index}]";
        DeliveryReceipt receipt;
        try
        {
            using (uploadContent)
            {
                var target = _dropFactory.Create(endpoint);
                var schemeNote = DropTargetFactory.DetectScheme(endpoint.OutBoxUrl, endpoint.FtpServer).ToUpperInvariant();

                using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                uploadCts.CancelAfter(TimeSpan.FromSeconds(_config.AseXml.DeliveryTimeoutSeconds));

                receipt = await target.UploadAsync(endpoint, remoteFileName, uploadContent, uploadCts.Token);

                steps.Add(TestStep.Pass(uploadAction,
                    $"Uploaded {uploadedAs} via {schemeNote} → {receipt.RemotePath} ({receipt.BytesWritten} bytes, {receipt.Duration.TotalMilliseconds:F0} ms)"));
            }
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Fail(uploadAction,
                $"Upload failed for {remoteFileName}: {ex.Message}"));
            return;
        }

        deliveries.Add(new Dictionary<string, object?>
        {
            ["messageId"] = messageId,
            ["transactionId"] = transactionId,
            ["endpointCode"] = endpoint.EndPointCode,
            ["remotePath"] = receipt.RemotePath,
            ["uploadedAs"] = uploadedAs,
            ["bytes"] = uploadBytes,
            ["status"] = "Delivered",
        });

        // 5) Post-delivery UI verifications (Phase 3)
        if (tc.PostDeliveryVerifications.Count > 0)
        {
            var context = BuildVerificationContext(
                messageId, transactionId, remoteFileName, uploadedAs,
                endpoint.EndPointCode, resolvedFields);

            for (var vIdx = 0; vIdx < tc.PostDeliveryVerifications.Count; vIdx++)
            {
                ct.ThrowIfCancellationRequested();
                await RunVerificationAsync(
                    tc.PostDeliveryVerifications[vIdx],
                    index, vIdx + 1,
                    remoteFileName,
                    context,
                    steps,
                    ct);
            }
        }
    }

    // ─────────────────────────────────────────────────────
    // Phase 3 — verifications
    // ─────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildVerificationContext(
        string messageId, string transactionId, string remoteFileName,
        string uploadedAs, string endpointCode,
        IReadOnlyDictionary<string, string> resolvedFields)
    {
        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MessageID"]     = messageId,
            ["TransactionID"] = transactionId,
            ["Filename"]      = remoteFileName,
            ["EndpointCode"]  = endpointCode,
            ["UploadedAs"]    = uploadedAs,
        };
        // Every resolved field (NMI, MeterSerial, DateIdentified, etc.) joins the context.
        // Dedicated keys above win if the render happens to share a name.
        foreach (var (k, v) in resolvedFields)
        {
            if (!ctx.ContainsKey(k)) ctx[k] = v;
        }
        return ctx;
    }

    private async Task RunVerificationAsync(
        VerificationStep v,
        int deliveryIndex,
        int verifyIndex,
        string remoteFileName,
        IReadOnlyDictionary<string, string> context,
        List<TestStep> steps,
        CancellationToken ct)
    {
        var waitAction = $"wait[{deliveryIndex}.{verifyIndex}]";
        if (v.WaitBeforeSeconds > 0)
        {
            steps.Add(TestStep.Pass(waitAction,
                $"Waiting {v.WaitBeforeSeconds}s for Bravo to process {remoteFileName} before '{v.Description}'"));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(v.WaitBeforeSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                steps.Add(TestStep.Err(waitAction, "Wait cancelled"));
                return;
            }
        }

        var verifyAction = $"verify[{deliveryIndex}.{verifyIndex}]";

        if (!Enum.TryParse<TestTargetType>(v.Target, ignoreCase: true, out var target))
        {
            steps.Add(TestStep.Fail(verifyAction,
                $"VerificationStep.Target '{v.Target}' is not a known TestTargetType."));
            return;
        }

        // Build a synthetic task for the sibling UI agent and preload a substituted test case.
        var syntheticTask = new TestTask
        {
            Description = v.Description,
            Target = target,
            Parameters = new Dictionary<string, object>(),
        };

        if (target is TestTargetType.UI_Web_MVC or TestTargetType.UI_Web_Blazor)
        {
            if (v.WebUi is null)
            {
                steps.Add(TestStep.Fail(verifyAction,
                    $"Target is '{v.Target}' but WebUi steps are missing on the VerificationStep."));
                return;
            }
            var clone = CloneAndSubstitute(v.WebUi, context);
            syntheticTask.Parameters["PreloadedTestCases"] = new List<WebUiTestCase> { clone.ToTestCase(v.Description) };
        }
        else if (target is TestTargetType.UI_Desktop_WinForms)
        {
            if (v.DesktopUi is null)
            {
                steps.Add(TestStep.Fail(verifyAction,
                    $"Target is '{v.Target}' but DesktopUi steps are missing on the VerificationStep."));
                return;
            }
            var clone = CloneAndSubstitute(v.DesktopUi, context);
            syntheticTask.Parameters["PreloadedTestCases"] = new List<DesktopUiTestCase> { clone.ToTestCase(v.Description) };
        }
        else
        {
            steps.Add(TestStep.Fail(verifyAction,
                $"Verification target '{v.Target}' is not a UI target; only UI_Web_MVC / UI_Web_Blazor / UI_Desktop_WinForms are supported."));
            return;
        }

        ITestAgent? sibling = null;
        foreach (var candidate in _siblings)
        {
            if (await candidate.CanHandleAsync(syntheticTask)) { sibling = candidate; break; }
        }
        if (sibling is null)
        {
            steps.Add(TestStep.Err(verifyAction,
                $"No registered agent can handle target '{v.Target}'. Is the UI agent registered in DI?"));
            return;
        }

        Logger.LogInformation(
            "[{Agent}] Running verification {Idx} '{Desc}' via {Sibling}",
            Name, verifyAction, v.Description, sibling.Name);

        TestResult childResult;
        try
        {
            childResult = await sibling.ExecuteAsync(syntheticTask, ct);
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(verifyAction,
                $"Verification agent '{sibling.Name}' threw: {ex.Message}"));
            return;
        }

        // Prefix each child step so the verification steps are clearly grouped.
        foreach (var childStep in childResult.Steps)
        {
            steps.Add(new TestStep
            {
                Action = $"{verifyAction} {childStep.Action}",
                Summary = childStep.Summary,
                Status = childStep.Status,
                Detail = childStep.Detail,
                Duration = childStep.Duration,
            });
        }

        // Surface the aggregate status from the verification so the delivery
        // objective reflects a failed verification as Failed (not Passed).
        if (childResult.Status is TestStatus.Failed or TestStatus.Error
            && childResult.Steps.Count == 0)
        {
            // Sibling reported status without steps — make that visible.
            steps.Add(new TestStep
            {
                Action = verifyAction,
                Summary = childResult.Summary,
                Status = childResult.Status,
            });
        }
    }

    private static WebUiTestDefinition CloneAndSubstitute(
        WebUiTestDefinition src, IReadOnlyDictionary<string, string> ctx)
    {
        var clone = new WebUiTestDefinition
        {
            Description = src.Description,
            StartUrl = TokenSubstituter.Substitute(src.StartUrl, ctx) ?? src.StartUrl,
            TakeScreenshotOnFailure = src.TakeScreenshotOnFailure,
            Steps = src.Steps.Select(s => new WebUiStep
            {
                Action    = s.Action,
                Selector  = TokenSubstituter.Substitute(s.Selector, ctx),
                Value     = TokenSubstituter.Substitute(s.Value,    ctx),
                TimeoutMs = s.TimeoutMs,
            }).ToList(),
        };
        return clone;
    }

    private static DesktopUiTestDefinition CloneAndSubstitute(
        DesktopUiTestDefinition src, IReadOnlyDictionary<string, string> ctx)
    {
        var clone = new DesktopUiTestDefinition
        {
            Description = src.Description,
            TakeScreenshotOnFailure = src.TakeScreenshotOnFailure,
            Steps = src.Steps.Select(s => new DesktopUiStep
            {
                Action       = s.Action,
                AutomationId = TokenSubstituter.Substitute(s.AutomationId, ctx),
                Name         = TokenSubstituter.Substitute(s.Name,         ctx),
                ClassName    = TokenSubstituter.Substitute(s.ClassName,    ctx),
                ControlType  = TokenSubstituter.Substitute(s.ControlType,  ctx),
                TreePath     = TokenSubstituter.Substitute(s.TreePath,     ctx),
                Value        = TokenSubstituter.Substitute(s.Value,        ctx),
                MenuPath     = TokenSubstituter.Substitute(s.MenuPath,     ctx),
                WindowTitle  = TokenSubstituter.Substitute(s.WindowTitle,  ctx),
                TimeoutMs    = s.TimeoutMs,
            }).ToList(),
        };
        return clone;
    }

    // ─────────────────────────────────────────────────────
    // LLM prompt
    // ─────────────────────────────────────────────────────

    private async Task<List<AseXmlDeliveryTestCase>?> GenerateTestCasesAsync(
        TestTask task, CancellationToken ct)
    {
        // Pull the endpoint code catalogue from the resolver so the LLM
        // picks from the real list in the target Bravo DB.
        IReadOnlyList<string> endpointCodes;
        try
        {
            endpointCodes = await _endpoints.ListCodesAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Could not load endpoint codes from mil.V2_MIL_EndPoint — LLM will see an empty catalogue. " +
                "Pass --endpoint <code> to supply one explicitly.");
            endpointCodes = Array.Empty<string>();
        }

        var templateCatalogue = BuildTemplateCatalogue();
        var endpointCatalogue = endpointCodes.Count > 0
            ? string.Join(", ", endpointCodes)
            : "(none discoverable — caller must supply --endpoint)";

        var prompt = $$"""
            You are generating aseXML DELIVERY test cases for an AEMO B2B test run.
            Each case names ONE template to render and ONE endpoint code to ship to.

            Objective:
            "{{task.Description}}"

            Available templates (copy templateId + transactionType verbatim):
            {{templateCatalogue}}

            Available endpoint codes (copy verbatim into endpointCode):
            {{endpointCatalogue}}

            RULES:
            - Read the objective LITERALLY. Return exactly one delivery case unless the
              objective explicitly asks for multiple variants.
            - Populate only fields listed as "source: user" in the chosen template.
            - NEVER set auto fields (MessageID, TransactionID, MessageDate, TransactionDate).
            - NEVER set const fields.
            - Populate endpointCode with the code named in the objective (e.g. "GatewaySPARQ"),
              matching verbatim one of the Available endpoint codes above.
              If the objective does not name an endpoint and none of the codes obviously
              matches, leave endpointCode as an empty string — the CLI may supply a default.
            - Keep the description short and specific (e.g. "Deliver MFN One In All In for NMI 4103035611 to GatewaySPARQ").

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
                "endpointCode": "GatewaySPARQ",
                "validateAgainstSchema": false
              }
            ]
            """;

        return await AskLlmForJsonAsync<List<AseXmlDeliveryTestCase>>(prompt, ct);
    }

    private string BuildTemplateCatalogue()
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
        return Path.Combine(root, $"{stamp}_{runId}_deliver");
    }

    private static string MakeFileSafe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.Trim().Replace(' ', '-');
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private static string BuildRenderDetail(
        AseXmlDeliveryTestCase tc, string txnType, string xmlLocalPath,
        IReadOnlyDictionary<string, string> resolvedFields, string xml)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Template: {tc.TemplateId} ({txnType})");
        sb.AppendLine($"Local:    {xmlLocalPath}");
        sb.AppendLine();
        sb.AppendLine("Resolved fields:");
        foreach (var (k, v) in resolvedFields.OrderBy(kv => kv.Key))
            sb.AppendLine($"  {k} = {v}");
        sb.AppendLine();
        var preview = xml.Length > 1200 ? xml[..1200] + "\n…[truncated]" : xml;
        sb.AppendLine("XML preview:");
        sb.Append(preview);
        return sb.ToString();
    }

    private TestResult BuildResult(
        TestTask task, List<TestStep> steps, TestStatus status, string summary,
        Stopwatch sw, List<AseXmlDeliveryTestDefinition> definitions,
        List<Dictionary<string, object?>> deliveries, string? outputDir = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["generatedTestCases"] = definitions,
            ["deliveries"] = deliveries,
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
