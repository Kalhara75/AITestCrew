using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.AseXmlAgent.Recording;
using AiTestCrew.Agents.DesktopUiBase;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Agents.WebUiBase;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Utilities;

namespace AiTestCrew.Agents.Recording;

/// <summary>
/// Shared recording implementation used by CLI flows (--record, --record-setup,
/// --record-verification, --auth-setup) and the agent queue's recording JobKinds.
/// Runs a Playwright or FlaUI session and persists captured steps back into the test set.
/// </summary>
public class RecordingService : IRecordingService
{
    private readonly TestEnvironmentConfig _cfg;
    private readonly IEnvironmentResolver _envResolver;
    private readonly IModuleRepository _moduleRepo;
    private readonly ITestSetRepository _testSetRepo;
    private readonly IExecutionHistoryRepository _historyRepo;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RecordingService> _logger;

    public RecordingService(
        TestEnvironmentConfig cfg,
        IEnvironmentResolver envResolver,
        IModuleRepository moduleRepo,
        ITestSetRepository testSetRepo,
        IExecutionHistoryRepository historyRepo,
        ILoggerFactory loggerFactory,
        ILogger<RecordingService> logger)
    {
        _cfg = cfg;
        _envResolver = envResolver;
        _moduleRepo = moduleRepo;
        _testSetRepo = testSetRepo;
        _historyRepo = historyRepo;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────
    // --record — standalone test case
    // ─────────────────────────────────────────────────────────────
    public async Task<RecordingResult> RecordCaseAsync(RecordCaseRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.ModuleId) || string.IsNullOrWhiteSpace(r.TestSetId))
            return Fail("moduleId and testSetId are required");
        if (string.IsNullOrWhiteSpace(r.CaseName))
            return Fail("caseName is required");

        var envKey = _envResolver.ResolveKey(r.EnvironmentKey);
        var target = string.IsNullOrWhiteSpace(r.Target) ? "UI_Web_MVC" : r.Target;
        var isDesktop = target.Equals("UI_Desktop_WinForms", StringComparison.OrdinalIgnoreCase);
        var recLogger = _loggerFactory.CreateLogger("Recorder");

        var moduleId  = SlugHelper.ToSlug(r.ModuleId);
        var testSetId = SlugHelper.ToSlug(r.TestSetId);

        if (!_moduleRepo.Exists(moduleId))
            await _moduleRepo.CreateAsync(r.ModuleId);

        var testSet = await _testSetRepo.LoadAsync(moduleId, testSetId)
                      ?? await _testSetRepo.CreateEmptyAsync(moduleId, r.TestSetId);

        var objectiveId = $"recorded-{SlugHelper.ToSlug(r.CaseName)}";

        if (isDesktop)
        {
            var appPath = _envResolver.ResolveWinFormsAppPath(envKey);
            var appArgs = _envResolver.ResolveWinFormsAppArgs(envKey);
            if (string.IsNullOrWhiteSpace(appPath))
                return Fail($"WinFormsAppPath not configured for environment '{envKey}'.");

            _logger.LogInformation("Recording desktop case '{CaseName}' on {Target} (env {EnvKey}, app {App})", r.CaseName, target, envKey, appPath);

            var recorded = await DesktopRecorder.RecordAsync(appPath, appArgs, r.CaseName, _cfg, recLogger);
            if (recorded.Steps.Count == 0)
                return Fail("No steps were captured. Test case not saved.");

            var step = DesktopUiTestDefinition.FromTestCase(recorded);
            var existingIdx = testSet.TestObjectives.FindIndex(o => o.Id == objectiveId);
            if (existingIdx >= 0)
            {
                testSet.TestObjectives[existingIdx].DesktopUiSteps.Add(step);
            }
            else
            {
                testSet.TestObjectives.Add(new TestObjective
                {
                    Id              = objectiveId,
                    Name            = r.CaseName,
                    ParentObjective = r.CaseName,
                    AgentName       = "WinForms Desktop UI Agent",
                    TargetType      = target,
                    Source          = "Recorded",
                    DesktopUiSteps  = [step]
                });
            }
            if (!testSet.Objectives.Contains(r.CaseName, StringComparer.OrdinalIgnoreCase))
                testSet.Objectives.Add(r.CaseName);
            await _testSetRepo.SaveAsync(testSet, moduleId);

            var stepSummaries = recorded.Steps
                .Select(s => new RecordedStepSummary(s.Action, AutomationId: s.AutomationId, Name: s.Name, Value: s.Value))
                .ToList();
            return new RecordingResult(true,
                $"Saved {recorded.Steps.Count} desktop step(s) to {moduleId}/{testSetId}",
                recorded.Steps.Count, Steps: stepSummaries);
        }

        // ── Web recording path ──
        var baseUrl = target.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? _envResolver.ResolveBraveCloudUiUrl(envKey)
            : _envResolver.ResolveLegacyWebUiUrl(envKey);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            var key = target.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase) ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
            return Fail($"{key} not configured for environment '{envKey}'.");
        }

        _logger.LogInformation("Recording web case '{CaseName}' on {Target} (env {EnvKey}, baseUrl {BaseUrl})", r.CaseName, target, envKey, baseUrl);

        var storageState = target.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? _envResolver.ResolveBraveCloudUiStorageStatePath(envKey)
            : _envResolver.ResolveLegacyWebUiStorageStatePath(envKey);
        if (!string.IsNullOrEmpty(storageState) && !Path.IsPathRooted(storageState))
            storageState = Path.Combine(AppContext.BaseDirectory, storageState);

        var webRecorded = await PlaywrightRecorder.RecordAsync(baseUrl, r.CaseName, _cfg, recLogger, storageState, target);
        if (webRecorded.Steps.Count == 0)
            return Fail("No steps were captured. Test case not saved.");

        var agentName = target.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
                        ? "Brave Cloud UI Agent" : "Legacy Web UI Agent";
        var uiStep = WebUiTestDefinition.FromTestCase(webRecorded);

        var webExistingIdx = testSet.TestObjectives.FindIndex(o => o.Id == objectiveId);
        if (webExistingIdx >= 0)
        {
            testSet.TestObjectives[webExistingIdx].WebUiSteps.Add(uiStep);
        }
        else
        {
            testSet.TestObjectives.Add(new TestObjective
            {
                Id              = objectiveId,
                Name            = r.CaseName,
                ParentObjective = r.CaseName,
                AgentName       = agentName,
                TargetType      = target,
                Source          = "Recorded",
                WebUiSteps      = [uiStep]
            });
        }
        if (!testSet.Objectives.Contains(r.CaseName, StringComparer.OrdinalIgnoreCase))
            testSet.Objectives.Add(r.CaseName);
        await _testSetRepo.SaveAsync(testSet, moduleId);

        var webSummaries = webRecorded.Steps
            .Select(s => new RecordedStepSummary(s.Action, Selector: s.Selector, Value: s.Value))
            .ToList();
        return new RecordingResult(true,
            $"Saved {webRecorded.Steps.Count} web step(s) to {moduleId}/{testSetId}",
            webRecorded.Steps.Count, Steps: webSummaries);
    }

    // ─────────────────────────────────────────────────────────────
    // --record-setup — reusable setup steps at test-set level
    // ─────────────────────────────────────────────────────────────
    public async Task<RecordingResult> RecordSetupAsync(RecordSetupRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.ModuleId) || string.IsNullOrWhiteSpace(r.TestSetId))
            return Fail("moduleId and testSetId are required");

        var envKey = _envResolver.ResolveKey(r.EnvironmentKey);
        var target = string.IsNullOrWhiteSpace(r.Target) ? "UI_Web_MVC" : r.Target;
        var baseUrl = target.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? _envResolver.ResolveBraveCloudUiUrl(envKey)
            : _envResolver.ResolveLegacyWebUiUrl(envKey);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            var key = target.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase) ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
            return Fail($"{key} not configured for environment '{envKey}'.");
        }

        var recLogger = _loggerFactory.CreateLogger("Recorder");
        _logger.LogInformation("Recording setup steps on {Target} (env {EnvKey}, baseUrl {BaseUrl})", target, envKey, baseUrl);

        var storageState = target.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? _envResolver.ResolveBraveCloudUiStorageStatePath(envKey) : null;
        if (!string.IsNullOrEmpty(storageState) && !Path.IsPathRooted(storageState))
            storageState = Path.Combine(AppContext.BaseDirectory, storageState);

        var recorded = await PlaywrightRecorder.RecordAsync(baseUrl, "setup", _cfg, recLogger, storageState, target);
        if (recorded.Steps.Count == 0)
            return Fail("No steps were captured. Setup steps not saved.");

        var moduleId  = SlugHelper.ToSlug(r.ModuleId);
        var testSetId = SlugHelper.ToSlug(r.TestSetId);

        if (!_moduleRepo.Exists(moduleId))
            await _moduleRepo.CreateAsync(r.ModuleId);

        var testSet = await _testSetRepo.LoadAsync(moduleId, testSetId)
                      ?? await _testSetRepo.CreateEmptyAsync(moduleId, r.TestSetId);
        testSet.SetupStartUrl = recorded.StartUrl;
        testSet.SetupSteps = recorded.Steps;
        await _testSetRepo.SaveAsync(testSet, moduleId);

        var stepSummaries = recorded.Steps
            .Select(s => new RecordedStepSummary(s.Action, Selector: s.Selector, Value: s.Value))
            .ToList();
        return new RecordingResult(true,
            $"Saved {recorded.Steps.Count} setup step(s) to {moduleId}/{testSetId}",
            recorded.Steps.Count, Steps: stepSummaries);
    }

    // ─────────────────────────────────────────────────────────────
    // --record-verification — post-delivery UI verification
    // ─────────────────────────────────────────────────────────────
    public async Task<RecordingResult> RecordVerificationAsync(RecordVerificationRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.ModuleId) || string.IsNullOrWhiteSpace(r.TestSetId) || string.IsNullOrWhiteSpace(r.ObjectiveId))
            return Fail("moduleId, testSetId, objectiveId are required");
        if (string.IsNullOrWhiteSpace(r.VerificationName))
            return Fail("verificationName is required");
        var target = string.IsNullOrWhiteSpace(r.Target) ? "UI_Web_Blazor" : r.Target;
        if (target is not ("UI_Web_MVC" or "UI_Web_Blazor" or "UI_Desktop_WinForms"))
            return Fail($"target must be UI_Web_MVC, UI_Web_Blazor, or UI_Desktop_WinForms (got '{target}')");

        var envKey = _envResolver.ResolveKey(r.EnvironmentKey);
        var moduleId  = SlugHelper.ToSlug(r.ModuleId);
        var testSetId = SlugHelper.ToSlug(r.TestSetId);
        var recLogger = _loggerFactory.CreateLogger("RecordVerification");

        var testSet = await _testSetRepo.LoadAsync(moduleId, testSetId);
        if (testSet is null)
            return Fail($"Test set '{testSetId}' not found in module '{moduleId}'.");

        var targetObjective = testSet.TestObjectives.FirstOrDefault(o =>
            string.Equals(o.Id, r.ObjectiveId, StringComparison.OrdinalIgnoreCase))
            ?? testSet.TestObjectives.FirstOrDefault(o =>
                string.Equals(o.Name, r.ObjectiveId, StringComparison.OrdinalIgnoreCase));
        if (targetObjective is null)
            return Fail($"Objective '{r.ObjectiveId}' not found in test set.");

        if (targetObjective.AseXmlDeliverySteps.Count == 0)
            return Fail($"Objective '{targetObjective.Id}' is not an AseXml_Deliver objective.");
        if (r.DeliveryStepIndex < 0 || r.DeliveryStepIndex >= targetObjective.AseXmlDeliverySteps.Count)
            return Fail($"deliveryStepIndex {r.DeliveryStepIndex} out of range (0..{targetObjective.AseXmlDeliverySteps.Count - 1}).");
        var deliveryCase = targetObjective.AseXmlDeliverySteps[r.DeliveryStepIndex];

        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in deliveryCase.FieldValues)
            if (!string.IsNullOrEmpty(v)) context[k] = v;

        var historyCtx = await _historyRepo.GetLatestDeliveryContextAsync(testSetId, moduleId, targetObjective.Id);
        if (historyCtx is null)
            return Fail($"No successful delivery found for objective '{targetObjective.Id}'. Run the delivery at least once so the recorder has real data to reference.");
        foreach (var (k, v) in historyCtx)
            if (!string.IsNullOrEmpty(v)) context[k] = v;

        _logger.LogInformation("Recording verification '{Name}' for {ObjId} (target {Target}, {CtxCount} context key(s))",
            r.VerificationName, targetObjective.Id, target, context.Count);

        var waitSeconds = r.WaitBeforeSeconds > 0 ? r.WaitBeforeSeconds : _cfg.AseXml.DefaultVerificationWaitSeconds;
        var verifyStep = new VerificationStep
        {
            Description = r.VerificationName,
            Target = target,
            WaitBeforeSeconds = waitSeconds,
        };

        if (target == "UI_Desktop_WinForms")
        {
            var appPath = _envResolver.ResolveWinFormsAppPath(envKey);
            var appArgs = _envResolver.ResolveWinFormsAppArgs(envKey);
            if (string.IsNullOrWhiteSpace(appPath))
                return Fail($"WinFormsAppPath not configured for environment '{envKey}'.");
            var dtRecorded = await DesktopRecorder.RecordAsync(appPath, appArgs, r.VerificationName, _cfg, recLogger);
            if (dtRecorded.Steps.Count == 0)
                return Fail("No steps captured. Verification not saved.");
            VerificationRecorderHelper.AutoParameteriseDesktopUi(dtRecorded, context, recLogger);
            verifyStep.DesktopUi = DesktopUiTestDefinition.FromTestCase(dtRecorded);
        }
        else
        {
            var baseUrl = target == "UI_Web_Blazor"
                ? _envResolver.ResolveBraveCloudUiUrl(envKey)
                : _envResolver.ResolveLegacyWebUiUrl(envKey);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                var key = target == "UI_Web_Blazor" ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
                return Fail($"{key} not configured for environment '{envKey}'.");
            }
            var storageState = target == "UI_Web_Blazor"
                ? _envResolver.ResolveBraveCloudUiStorageStatePath(envKey)
                : _envResolver.ResolveLegacyWebUiStorageStatePath(envKey);
            if (!string.IsNullOrEmpty(storageState) && !Path.IsPathRooted(storageState))
                storageState = Path.Combine(AppContext.BaseDirectory, storageState);
            if (string.IsNullOrEmpty(storageState))
                _logger.LogInformation("No cached auth state for {Target}/{EnvKey}; recorder will start unauthenticated.", target, envKey);

            var webRecorded = await PlaywrightRecorder.RecordAsync(baseUrl, r.VerificationName, _cfg, recLogger, storageState, target);
            if (webRecorded.Steps.Count == 0)
                return Fail("No steps captured. Verification not saved.");
            VerificationRecorderHelper.AutoParameteriseWebUi(webRecorded, context, recLogger);
            verifyStep.WebUi = WebUiTestDefinition.FromTestCase(webRecorded);
        }

        deliveryCase.PostDeliveryVerifications.Add(verifyStep);
        await _testSetRepo.SaveAsync(testSet, moduleId);

        return new RecordingResult(true,
            $"Saved verification '{r.VerificationName}' to {targetObjective.Id}. " +
            $"Delivery case now has {deliveryCase.PostDeliveryVerifications.Count} verification(s).",
            1);
    }

    // ─────────────────────────────────────────────────────────────
    // --auth-setup — interactive login → storage state
    // ─────────────────────────────────────────────────────────────
    public async Task<RecordingResult> AuthSetupAsync(AuthSetupRequest r, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(r.Target) ? "UI_Web_Blazor" : r.Target;
        var isLegacy = target.Equals("UI_Web_MVC", StringComparison.OrdinalIgnoreCase);
        var envKey = _envResolver.ResolveKey(r.EnvironmentKey);
        var envDisplay = _envResolver.ResolveDisplayName(envKey);

        var baseUrl = isLegacy
            ? _envResolver.ResolveLegacyWebUiUrl(envKey)
            : _envResolver.ResolveBraveCloudUiUrl(envKey);
        var statePath = isLegacy
            ? _envResolver.ResolveLegacyWebUiStorageStatePath(envKey)
            : _envResolver.ResolveBraveCloudUiStorageStatePath(envKey);
        var maxAgeHours = isLegacy ? _cfg.LegacyWebUiStorageStateMaxAgeHours : _cfg.BraveCloudUiStorageStateMaxAgeHours;
        if (!string.IsNullOrEmpty(statePath) && !Path.IsPathRooted(statePath))
            statePath = Path.Combine(AppContext.BaseDirectory, statePath);

        var urlKey  = isLegacy ? "LegacyWebUiUrl" : "BraveCloudUiUrl";
        var pathKey = isLegacy ? "LegacyWebUiStorageStatePath" : "BraveCloudUiStorageStatePath";
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Fail($"{urlKey} not configured for environment '{envKey}'.");
        if (string.IsNullOrWhiteSpace(statePath))
            return Fail($"{pathKey} not configured for environment '{envKey}'.");

        _logger.LogInformation("Auth setup — {Target} for env {EnvDisplay} ({EnvKey}), URL {Url}", target, envDisplay, envKey, baseUrl);

        using var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            SlowMo = 50,
            Args = ["--start-maximized"]
        });

        try
        {
            var browserCtx = await browser.NewContextAsync(
                new BrowserNewContextOptions { ViewportSize = ViewportSize.NoViewport });
            var page = await browserCtx.NewPageAsync();

            var navigateUrl = isLegacy
                ? $"{baseUrl.TrimEnd('/')}{_cfg.LegacyWebUiLoginPath}"
                : baseUrl;
            await page.GotoAsync(navigateUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            _logger.LogInformation("Waiting for login to complete (up to 3 minutes). Do not close the browser.");

            var initialUrl = page.Url;
            var loginPath = _cfg.LegacyWebUiLoginPath.TrimStart('/');
            Func<string, bool> isLoggedIn = isLegacy
                ? (string.IsNullOrEmpty(loginPath)
                    ? url => !string.Equals(url, initialUrl, StringComparison.OrdinalIgnoreCase)
                             && !url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                    : url => !url.Contains(loginPath, StringComparison.OrdinalIgnoreCase))
                : url => url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase)
                         && !url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (isLoggedIn(page.Url)) break;
                await Task.Delay(500, ct);
            }

            if (!isLoggedIn(page.Url))
                return Fail("Timed out waiting for login to complete.");

            await Task.Delay(1000, ct);
            var dir = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await browserCtx.StorageStateAsync(new BrowserContextStorageStateOptions { Path = statePath });

            return new RecordingResult(true,
                $"Auth state saved → {statePath} (valid for {maxAgeHours} hours)");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Browser was closed before auth state could be saved. Run again and wait for confirmation.");
        }
        finally
        {
            if (browser.IsConnected) await browser.CloseAsync();
        }
    }

    private static RecordingResult Fail(string reason) => new(false, reason, 0, reason);
}
