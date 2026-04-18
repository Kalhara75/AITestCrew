using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Spectre.Console;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.AseXmlAgent.Recording;
using AiTestCrew.Agents.AseXmlAgent.Templates;
using AiTestCrew.Agents.Auth;
using AiTestCrew.Agents.BraveCloudUiAgent;
using AiTestCrew.Agents.DesktopUiBase;
using AiTestCrew.Agents.LegacyWebUiAgent;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Agents.WebUiBase;
using AiTestCrew.Agents.WinFormsUiAgent;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Core.Services;
using AiTestCrew.Orchestrator;
using AiTestCrew.Runner;

// ── Banner ──
AnsiConsole.Write(new FigletText("AI Test Crew").Color(Color.Cyan1));

// ── Parse CLI arguments ──
var cli = ParseArgs(args);

// ── Resolve repos for short-circuit commands (before the DI host is built) ──
var quickConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build()
    .GetSection("TestEnvironment")
    .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();

IModuleRepository ResolveModuleRepo()
{
    if (!string.IsNullOrWhiteSpace(quickConfig.ServerUrl))
        return new AiTestCrew.Runner.RemoteRepositories.ApiClientModuleRepository(
            new AiTestCrew.Runner.RemoteRepositories.RemoteHttpClient(quickConfig.ServerUrl, quickConfig.ApiKey));
    return new ModuleRepository(AppContext.BaseDirectory);
}

ITestSetRepository ResolveTsRepo()
{
    if (!string.IsNullOrWhiteSpace(quickConfig.ServerUrl))
        return new AiTestCrew.Runner.RemoteRepositories.ApiClientTestSetRepository(
            new AiTestCrew.Runner.RemoteRepositories.RemoteHttpClient(quickConfig.ServerUrl, quickConfig.ApiKey));
    return new TestSetRepository(AppContext.BaseDirectory);
}

IExecutionHistoryRepository ResolveHistRepo()
{
    if (!string.IsNullOrWhiteSpace(quickConfig.ServerUrl))
        return new AiTestCrew.Runner.RemoteRepositories.ApiClientExecutionHistoryRepository(
            new AiTestCrew.Runner.RemoteRepositories.RemoteHttpClient(quickConfig.ServerUrl, quickConfig.ApiKey));
    return new ExecutionHistoryRepository(AppContext.BaseDirectory);
}

// ── Short-circuit commands that don't need the LLM host ──
if (cli.Mode == RunMode.List)
{
    var repo = ResolveTsRepo();
    var histRepo = ResolveHistRepo();
    var sets = repo.ListAll();

    if (sets.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No saved test sets found.[/]");
        return;
    }

    var listTable = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]ID[/]")
        .AddColumn("[bold]Module[/]")
        .AddColumn("[bold]Objective[/]")
        .AddColumn("[bold]Objectives[/]")
        .AddColumn("[bold]Created (UTC)[/]")
        .AddColumn("[bold]Last Run (UTC)[/]")
        .AddColumn("[bold]Runs[/]");

    foreach (var s in sets)
    {
        var shortObjective = s.GetDisplayName(s.Objective);
        listTable.AddRow(
            s.Id,
            s.ModuleId.Length > 0 ? s.ModuleId : "-",
            shortObjective,
            s.TestObjectives.Count.ToString(),
            s.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            s.LastRunAt == default ? "-" : s.LastRunAt.ToString("yyyy-MM-dd HH:mm"),
            histRepo.CountRuns(s.Id).ToString()
        );
    }

    AnsiConsole.Write(listTable);
    AnsiConsole.MarkupLine("[grey]Re-run a saved set:  dotnet run -- --reuse <id>[/]");
    AnsiConsole.MarkupLine("[grey]Regenerate & resave: dotnet run -- --rebaseline \"<objective>\"[/]");
    return;
}

if (cli.ListModules)
{
    var moduleRepo = ResolveModuleRepo();
    var tsRepo = ResolveTsRepo();
    var modules = await moduleRepo.ListAllAsync();

    if (modules.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No modules found.[/]");
        AnsiConsole.MarkupLine("[grey]Create one: dotnet run -- --create-module \"Module Name\"[/]");
        return;
    }

    var moduleTable = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]ID[/]")
        .AddColumn("[bold]Name[/]")
        .AddColumn("[bold]Test Sets[/]")
        .AddColumn("[bold]Test Cases[/]")
        .AddColumn("[bold]Created (UTC)[/]");

    foreach (var m in modules)
    {
        var testSets = tsRepo.ListByModule(m.Id);
        moduleTable.AddRow(
            m.Id,
            m.Name,
            testSets.Count.ToString(),
            testSets.Sum(ts => ts.TestObjectives.Count).ToString(),
            m.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        );
    }

    AnsiConsole.Write(moduleTable);
    return;
}

if (cli.CreateModuleName is not null)
{
    var moduleRepo = ResolveModuleRepo();
    var module = await moduleRepo.CreateAsync(cli.CreateModuleName);
    AnsiConsole.MarkupLine($"[green]Module created:[/] {module.Id} ({module.Name})");
    AnsiConsole.MarkupLine($"[grey]Create a test set: dotnet run -- --create-testset {module.Id} \"Test Set Name\"[/]");
    return;
}

if (cli.CreateTestSetModuleId is not null && cli.CreateTestSetName is not null)
{
    var moduleRepo = ResolveModuleRepo();
    if (!moduleRepo.Exists(cli.CreateTestSetModuleId))
    {
        AnsiConsole.MarkupLine($"[red]Module '{cli.CreateTestSetModuleId}' not found.[/]");
        return;
    }
    var tsRepo = ResolveTsRepo();
    var testSet = await tsRepo.CreateEmptyAsync(cli.CreateTestSetModuleId, cli.CreateTestSetName);
    AnsiConsole.MarkupLine($"[green]Test set created:[/] {cli.CreateTestSetModuleId}/{testSet.Id} ({testSet.Name})");
    AnsiConsole.MarkupLine($"[grey]Run objective: dotnet run -- --module {cli.CreateTestSetModuleId} --testset {testSet.Id} \"<objective>\"[/]");
    return;
}

// ── Migrate existing JSON data to SQLite ──
if (cli.MigrateToSqlite)
{
    var migrateConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()
        .GetSection("TestEnvironment")
        .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();

    if (string.IsNullOrWhiteSpace(migrateConfig.SqliteConnectionString))
    {
        AnsiConsole.MarkupLine("[red]SqliteConnectionString is not configured in appsettings.json → TestEnvironment.[/]");
        AnsiConsole.MarkupLine("[grey]Set TestEnvironment.SqliteConnectionString, e.g. \"Data Source=C:/data/aitestcrew.db\"[/]");
        return;
    }

    var connFactory = new AiTestCrew.Agents.Persistence.Sqlite.SqliteConnectionFactory(migrateConfig.SqliteConnectionString);
    var sqliteModules = new AiTestCrew.Agents.Persistence.Sqlite.SqliteModuleRepository(connFactory);
    var sqliteTestSets = new AiTestCrew.Agents.Persistence.Sqlite.SqliteTestSetRepository(connFactory);
    var sqliteHistory = new AiTestCrew.Agents.Persistence.Sqlite.SqliteExecutionHistoryRepository(connFactory, migrateConfig.MaxExecutionRunsPerTestSet);

    // Source: file-based repos in the current data directory
    var fileModules = new ModuleRepository(AppContext.BaseDirectory);
    var fileTestSets = new TestSetRepository(AppContext.BaseDirectory);
    var fileHistory = new ExecutionHistoryRepository(AppContext.BaseDirectory);

    int moduleCount = 0, tsCount = 0, runCount = 0;

    // Migrate modules
    foreach (var m in await fileModules.ListAllAsync())
    {
        if (!sqliteModules.Exists(m.Id))
        {
            await sqliteModules.CreateAsync(m.Name, m.Description);
            moduleCount++;
        }
    }

    // Migrate test sets (both legacy and module-scoped)
    foreach (var ts in fileTestSets.ListAll())
    {
        var mid = string.IsNullOrEmpty(ts.ModuleId) ? "" : ts.ModuleId;
        await sqliteTestSets.SaveAsync(ts, mid);
        tsCount++;

        // Migrate execution history for this test set
        foreach (var run in fileHistory.ListRuns(ts.Id))
        {
            await sqliteHistory.SaveAsync(run);
            runCount++;
        }
    }

    AnsiConsole.MarkupLine($"[green]Migration complete:[/] {moduleCount} modules, {tsCount} test sets, {runCount} execution runs");
    AnsiConsole.MarkupLine($"[grey]Database: {migrateConfig.SqliteConnectionString}[/]");
    AnsiConsole.MarkupLine("[grey]Set TestEnvironment.StorageProvider = \"Sqlite\" to switch over.[/]");
    return;
}

// ── Record mode — human-driven capture, no LLM needed ──
if (cli.RecordMode)
{
    if (cli.ModuleId is null || cli.TestSetId is null)
    {
        AnsiConsole.MarkupLine("[red]--record requires --module <id> and --testset <id>[/]");
        return;
    }
    if (string.IsNullOrWhiteSpace(cli.CaseName))
    {
        AnsiConsole.MarkupLine("[red]--record requires --case-name \"<name>\"[/]");
        return;
    }

    // Load config to get base URL
    var recordConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()
        .GetSection("TestEnvironment")
        .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();
    var recordEnvResolver = new AiTestCrew.Agents.Environment.EnvironmentResolver(recordConfig);
    var recordEnvKey = recordEnvResolver.ResolveKey(cli.EnvironmentKey);

    var targetType = cli.RecordTarget ?? "UI_Web_MVC";
    var isDesktop = targetType.Equals("UI_Desktop_WinForms", StringComparison.OrdinalIgnoreCase);

    using var loggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var recLogger = loggerFactory.CreateLogger("Recorder");

    // Resolve slugified IDs so the saved file matches what the WebApi/UI expects
    var moduleId  = SlugHelper.ToSlug(cli.ModuleId);
    var testSetId = SlugHelper.ToSlug(cli.TestSetId);

    // Ensure the module and manifest exist (idempotent)
    var modRepo = ResolveModuleRepo();
    if (!modRepo.Exists(moduleId))
        await modRepo.CreateAsync(cli.ModuleId);

    // Save into the test set
    var tsRepo = ResolveTsRepo();
    var testSet = await tsRepo.LoadAsync(moduleId, testSetId)
                  ?? await tsRepo.CreateEmptyAsync(moduleId, cli.TestSetId);

    var objectiveId = $"recorded-{SlugHelper.ToSlug(cli.CaseName)}";

    if (isDesktop)
    {
        // ── Desktop recording path ──
        var desktopAppPath = recordEnvResolver.ResolveWinFormsAppPath(recordEnvKey);
        var desktopAppArgs = recordEnvResolver.ResolveWinFormsAppArgs(recordEnvKey);
        if (string.IsNullOrWhiteSpace(desktopAppPath))
        {
            AnsiConsole.MarkupLine($"[red]Application path not configured for environment '{recordEnvKey}' (or at the top level). Set 'WinFormsAppPath' in appsettings.json.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Recording[/] → {cli.ModuleId}/{cli.TestSetId}  case: [bold]{cli.CaseName}[/]");
        AnsiConsole.MarkupLine($"[grey]Environment: {recordEnvResolver.ResolveDisplayName(recordEnvKey)} ({recordEnvKey})[/]");
        AnsiConsole.MarkupLine($"[grey]Target: {targetType}  App: {desktopAppPath}[/]\n");

        var desktopRecorded = await DesktopRecorder.RecordAsync(
            desktopAppPath, desktopAppArgs,
            cli.CaseName, recordConfig, recLogger);

        if (desktopRecorded.Steps.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No steps were captured. Test case not saved.[/]");
            return;
        }

        var desktopStep = DesktopUiTestDefinition.FromTestCase(desktopRecorded);
        var existingIdx = testSet.TestObjectives.FindIndex(o => o.Id == objectiveId);

        if (existingIdx >= 0)
        {
            testSet.TestObjectives[existingIdx].DesktopUiSteps.Add(desktopStep);
        }
        else
        {
            var testObj = new AiTestCrew.Agents.Persistence.TestObjective
            {
                Id              = objectiveId,
                Name            = cli.CaseName,
                ParentObjective = cli.CaseName,
                AgentName       = "WinForms Desktop UI Agent",
                TargetType      = targetType,
                Source          = "Recorded",
                DesktopUiSteps  = [desktopStep]
            };
            testSet.TestObjectives.Add(testObj);
        }

        if (!testSet.Objectives.Contains(cli.CaseName, StringComparer.OrdinalIgnoreCase))
            testSet.Objectives.Add(cli.CaseName);

        await tsRepo.SaveAsync(testSet, moduleId);

        // Print captured steps
        var stepTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]#[/]")
            .AddColumn("[bold]Action[/]")
            .AddColumn("[bold]AutomationId[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Value[/]");

        for (int i = 0; i < desktopRecorded.Steps.Count; i++)
        {
            var s = desktopRecorded.Steps[i];
            var displayValue = s.Value is null ? "-" : (s.Value.Length > 40 ? s.Value[..40] + "..." : s.Value);
            stepTable.AddRow(
                (i + 1).ToString(),
                Markup.Escape(s.Action),
                Markup.Escape(s.AutomationId ?? "-"),
                Markup.Escape(s.Name ?? "-"),
                Markup.Escape(displayValue)
            );
        }
        AnsiConsole.Write(stepTable);
        AnsiConsole.MarkupLine($"\n[green]Saved[/] {desktopRecorded.Steps.Count} steps -> {Markup.Escape(moduleId)}/{Markup.Escape(testSetId)}");
        AnsiConsole.MarkupLine($"[grey]Replay: dotnet run -- --reuse {Markup.Escape(testSetId)}[/]");
        return;
    }

    // ── Web recording path (existing) ──
    var baseUrl = targetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
        ? recordEnvResolver.ResolveBraveCloudUiUrl(recordEnvKey)
        : recordEnvResolver.ResolveLegacyWebUiUrl(recordEnvKey);

    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        var key = targetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
        AnsiConsole.MarkupLine($"[red]Base URL not configured for environment '{recordEnvKey}'. Set '{key}' in the env block (or at the top level).[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[cyan]Recording[/] → {cli.ModuleId}/{cli.TestSetId}  case: [bold]{cli.CaseName}[/]");
    AnsiConsole.MarkupLine($"[grey]Environment: {recordEnvResolver.ResolveDisplayName(recordEnvKey)} ({recordEnvKey})[/]");
    AnsiConsole.MarkupLine($"[grey]Target: {targetType}  Base URL: {baseUrl}[/]\n");

    // For Blazor targets, pass the saved auth state so the recorder starts authenticated
    var recordStorageState = targetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
        ? recordEnvResolver.ResolveBraveCloudUiStorageStatePath(recordEnvKey) : null;
    if (!string.IsNullOrEmpty(recordStorageState) && !Path.IsPathRooted(recordStorageState))
        recordStorageState = Path.Combine(AppContext.BaseDirectory, recordStorageState);
    var recorded = await PlaywrightRecorder.RecordAsync(baseUrl, cli.CaseName, recordConfig, recLogger, recordStorageState, targetType);

    if (recorded.Steps.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No steps were captured. Test case not saved.[/]");
        return;
    }

    var agentName = targetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
                    ? "Brave Cloud UI Agent" : "Legacy Web UI Agent";

    // Check if an objective with this ID already exists — update it, otherwise add
    var webExistingIdx = testSet.TestObjectives.FindIndex(o => o.Id == objectiveId);
    var uiStep = WebUiTestDefinition.FromTestCase(recorded);

    if (webExistingIdx >= 0)
    {
        testSet.TestObjectives[webExistingIdx].WebUiSteps.Add(uiStep);
    }
    else
    {
        var testObj = new AiTestCrew.Agents.Persistence.TestObjective
        {
            Id              = objectiveId,
            Name            = cli.CaseName,
            ParentObjective = cli.CaseName,
            AgentName       = agentName,
            TargetType      = targetType,
            Source          = "Recorded",
            WebUiSteps      = [uiStep]
        };
        testSet.TestObjectives.Add(testObj);
    }

    if (!testSet.Objectives.Contains(cli.CaseName, StringComparer.OrdinalIgnoreCase))
        testSet.Objectives.Add(cli.CaseName);

    await tsRepo.SaveAsync(testSet, moduleId);

    // Print captured steps
    var stepTable2 = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]#[/]")
        .AddColumn("[bold]Action[/]")
        .AddColumn("[bold]Selector[/]")
        .AddColumn("[bold]Value[/]");

    for (int i = 0; i < recorded.Steps.Count; i++)
    {
        var s = recorded.Steps[i];
        var displayValue = s.Value is null ? "-" : (s.Value.Length > 40 ? s.Value[..40] + "..." : s.Value);
        stepTable2.AddRow(
            (i + 1).ToString(),
            Markup.Escape(s.Action),
            Markup.Escape(s.Selector ?? "-"),
            Markup.Escape(displayValue)
        );
    }
    AnsiConsole.Write(stepTable2);
    AnsiConsole.MarkupLine($"\n[green]Saved[/] {recorded.Steps.Count} steps -> {Markup.Escape(moduleId)}/{Markup.Escape(testSetId)}");
    AnsiConsole.MarkupLine($"[grey]Replay: dotnet run -- --reuse {Markup.Escape(testSetId)}[/]");
    return;
}

// ── Record-verification mode — Phase 3: capture UI steps and attach them to a delivery test case ──
if (cli.RecordVerification)
{
    if (cli.ModuleId is null || cli.TestSetId is null || cli.ObjectiveId is null)
    {
        AnsiConsole.MarkupLine("[red]--record-verification requires --module <id> --testset <id> --objective <objectiveId>[/]");
        return;
    }
    if (string.IsNullOrWhiteSpace(cli.VerificationName))
    {
        AnsiConsole.MarkupLine("[red]--record-verification requires --verification-name \"<name>\"[/]");
        return;
    }
    var verifyTarget = cli.RecordTarget ?? "UI_Web_Blazor";
    if (verifyTarget is not ("UI_Web_MVC" or "UI_Web_Blazor" or "UI_Desktop_WinForms"))
    {
        AnsiConsole.MarkupLine($"[red]--target must be UI_Web_MVC, UI_Web_Blazor, or UI_Desktop_WinForms (got '{verifyTarget}').[/]");
        return;
    }

    var verifyConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()
        .GetSection("TestEnvironment")
        .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();
    var verifyEnvResolver = new AiTestCrew.Agents.Environment.EnvironmentResolver(verifyConfig);
    var verifyEnvKey = verifyEnvResolver.ResolveKey(cli.EnvironmentKey);

    using var verifyLoggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var verifyLogger = verifyLoggerFactory.CreateLogger("RecordVerification");

    var vModuleId  = SlugHelper.ToSlug(cli.ModuleId);
    var vTestSetId = SlugHelper.ToSlug(cli.TestSetId);

    var vTsRepo = ResolveTsRepo();
    var vHistRepo = ResolveHistRepo();
    var vTestSet = await vTsRepo.LoadAsync(vModuleId, vTestSetId);
    if (vTestSet is null)
    {
        AnsiConsole.MarkupLine($"[red]Test set '{vTestSetId}' not found in module '{vModuleId}'.[/]");
        return;
    }

    // Match by Id (slug) first, then by Name (display name) case-insensitively —
    // same convention as --reuse --objective so users can pass either form.
    var targetObjective = vTestSet.TestObjectives.FirstOrDefault(o =>
        string.Equals(o.Id, cli.ObjectiveId, StringComparison.OrdinalIgnoreCase))
        ?? vTestSet.TestObjectives.FirstOrDefault(o =>
            string.Equals(o.Name, cli.ObjectiveId, StringComparison.OrdinalIgnoreCase));
    if (targetObjective is null)
    {
        var known = vTestSet.TestObjectives.Count > 0
            ? string.Join("; ", vTestSet.TestObjectives.Select(o =>
                string.IsNullOrWhiteSpace(o.Name) || o.Name == o.Id
                    ? o.Id
                    : $"{o.Id} (\"{o.Name}\")"))
            : "(none)";
        AnsiConsole.MarkupLine($"[red]Objective '{Markup.Escape(cli.ObjectiveId!)}' not found in test set. Available: {Markup.Escape(known)}[/]");
        return;
    }

    if (targetObjective.AseXmlDeliverySteps.Count == 0)
    {
        AnsiConsole.MarkupLine(
            $"[red]Objective '{targetObjective.Id}' is not an AseXml_Deliver objective; recording verifications is not supported for other targets.[/]");
        return;
    }
    if (cli.DeliveryStepIndex < 0 || cli.DeliveryStepIndex >= targetObjective.AseXmlDeliverySteps.Count)
    {
        AnsiConsole.MarkupLine(
            $"[red]--delivery-step-index {cli.DeliveryStepIndex} out of range (0..{targetObjective.AseXmlDeliverySteps.Count - 1}).[/]");
        return;
    }
    var deliveryCase = targetObjective.AseXmlDeliverySteps[cli.DeliveryStepIndex];

    // Build the recording context — user field values + latest successful delivery's MessageID/etc.
    var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (k, v) in deliveryCase.FieldValues)
        if (!string.IsNullOrEmpty(v)) context[k] = v;

    var historyCtx = await vHistRepo.GetLatestDeliveryContextAsync(vTestSetId, vModuleId, targetObjective.Id);
    if (historyCtx is null)
    {
        AnsiConsole.MarkupLine(
            $"[red]No successful delivery found for objective '{targetObjective.Id}'. " +
            $"Run the delivery at least once first so the recorder has real data to reference.[/]");
        return;
    }
    foreach (var (k, v) in historyCtx)
        if (!string.IsNullOrEmpty(v)) context[k] = v;

    AnsiConsole.MarkupLine($"[cyan]Recording verification[/] for {Markup.Escape(targetObjective.Id)} → target [bold]{verifyTarget}[/]");
    AnsiConsole.MarkupLine($"[grey]Auto-parameterise context ({context.Count} key(s)):[/]");
    foreach (var (k, v) in context.OrderBy(kv => kv.Key))
        AnsiConsole.MarkupLine($"[grey]  {{{{{Markup.Escape(k)}}}}} = {Markup.Escape(v)}[/]");
    AnsiConsole.WriteLine();

    var waitSeconds = cli.VerificationWait ?? verifyConfig.AseXml.DefaultVerificationWaitSeconds;

    var verifyStep = new AiTestCrew.Agents.AseXmlAgent.VerificationStep
    {
        Description = cli.VerificationName!,
        Target = verifyTarget,
        WaitBeforeSeconds = waitSeconds,
    };

    if (verifyTarget == "UI_Desktop_WinForms")
    {
        var verifyAppPath = verifyEnvResolver.ResolveWinFormsAppPath(verifyEnvKey);
        var verifyAppArgs = verifyEnvResolver.ResolveWinFormsAppArgs(verifyEnvKey);
        if (string.IsNullOrWhiteSpace(verifyAppPath))
        {
            AnsiConsole.MarkupLine($"[red]WinFormsAppPath not configured for environment '{verifyEnvKey}'.[/]");
            return;
        }
        var dtRecorded = await DesktopRecorder.RecordAsync(
            verifyAppPath, verifyAppArgs,
            cli.VerificationName!, verifyConfig, verifyLogger);
        if (dtRecorded.Steps.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No steps captured. Verification not saved.[/]");
            return;
        }
        VerificationRecorderHelper.AutoParameteriseDesktopUi(dtRecorded, context, verifyLogger);
        verifyStep.DesktopUi = DesktopUiTestDefinition.FromTestCase(dtRecorded);
    }
    else
    {
        var verifyBaseUrl = verifyTarget == "UI_Web_Blazor"
            ? verifyEnvResolver.ResolveBraveCloudUiUrl(verifyEnvKey)
            : verifyEnvResolver.ResolveLegacyWebUiUrl(verifyEnvKey);
        if (string.IsNullOrWhiteSpace(verifyBaseUrl))
        {
            var key = verifyTarget == "UI_Web_Blazor" ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
            AnsiConsole.MarkupLine($"[red]Base URL not configured for environment '{verifyEnvKey}'. Set '{key}' in the env block (or at the top level).[/]");
            return;
        }
        // Pass the matching cached auth state so the recorder starts authenticated.
        // Run --auth-setup --target UI_Web_MVC|UI_Web_Blazor first to populate these.
        var verifyStorageState = verifyTarget == "UI_Web_Blazor"
            ? verifyEnvResolver.ResolveBraveCloudUiStorageStatePath(verifyEnvKey)
            : verifyEnvResolver.ResolveLegacyWebUiStorageStatePath(verifyEnvKey);
        if (!string.IsNullOrEmpty(verifyStorageState) && !Path.IsPathRooted(verifyStorageState))
            verifyStorageState = Path.Combine(AppContext.BaseDirectory, verifyStorageState);
        if (string.IsNullOrEmpty(verifyStorageState))
        {
            var setupKey = verifyTarget == "UI_Web_Blazor" ? "BraveCloudUiStorageStatePath" : "LegacyWebUiStorageStatePath";
            AnsiConsole.MarkupLine(
                $"[grey]No '{setupKey}' configured for env '{verifyEnvKey}' — recorder will start unauthenticated. " +
                $"Run --auth-setup --target {verifyTarget} --environment {verifyEnvKey} first to skip the login flow during recording.[/]");
        }
        var webRecorded = await PlaywrightRecorder.RecordAsync(
            verifyBaseUrl, cli.VerificationName!, verifyConfig, verifyLogger,
            verifyStorageState, verifyTarget);
        if (webRecorded.Steps.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No steps captured. Verification not saved.[/]");
            return;
        }
        VerificationRecorderHelper.AutoParameteriseWebUi(webRecorded, context, verifyLogger);
        verifyStep.WebUi = WebUiTestDefinition.FromTestCase(webRecorded);
    }

    deliveryCase.PostDeliveryVerifications.Add(verifyStep);
    await vTsRepo.SaveAsync(vTestSet, vModuleId);

    AnsiConsole.MarkupLine(
        $"\n[green]Saved verification[/] '{Markup.Escape(cli.VerificationName!)}' to {Markup.Escape(targetObjective.Id)}");
    AnsiConsole.MarkupLine(
        $"[grey]Delivery case now has {deliveryCase.PostDeliveryVerifications.Count} verification(s). " +
        $"Replay: dotnet run -- --reuse {Markup.Escape(vTestSetId)} --module {Markup.Escape(vModuleId)}[/]");
    return;
}

// ── Auth-setup mode — perform SSO login (with optional manual 2FA) and save browser auth state ──
if (cli.AuthSetupMode)
{
    var authConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()
        .GetSection("TestEnvironment")
        .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();

    var authEnvResolver = new AiTestCrew.Agents.Environment.EnvironmentResolver(authConfig);
    var authEnvKey = authEnvResolver.ResolveKey(cli.EnvironmentKey);
    var authEnvDisplay = authEnvResolver.ResolveDisplayName(authEnvKey);

    var authTargetType = cli.RecordTarget ?? "UI_Web_Blazor";
    var isLegacy = authTargetType.Equals("UI_Web_MVC", StringComparison.OrdinalIgnoreCase);
    var authBaseUrl = isLegacy
        ? authEnvResolver.ResolveLegacyWebUiUrl(authEnvKey)
        : authEnvResolver.ResolveBraveCloudUiUrl(authEnvKey);
    // Resolve relative path against bin dir so the file lands where the agent looks for it
    var authStatePath = isLegacy
        ? authEnvResolver.ResolveLegacyWebUiStorageStatePath(authEnvKey)
        : authEnvResolver.ResolveBraveCloudUiStorageStatePath(authEnvKey);
    var authMaxAgeHours = isLegacy ? authConfig.LegacyWebUiStorageStateMaxAgeHours : authConfig.BraveCloudUiStorageStateMaxAgeHours;
    if (!string.IsNullOrEmpty(authStatePath) && !Path.IsPathRooted(authStatePath))
        authStatePath = Path.Combine(AppContext.BaseDirectory, authStatePath);

    var urlConfigKey = isLegacy ? "LegacyWebUiUrl" : "BraveCloudUiUrl";
    var pathConfigKey = isLegacy ? "LegacyWebUiStorageStatePath" : "BraveCloudUiStorageStatePath";
    if (string.IsNullOrWhiteSpace(authBaseUrl))
    {
        AnsiConsole.MarkupLine($"[red]{urlConfigKey} not configured for environment '{authEnvKey}' (or at the top level).[/]");
        return;
    }
    if (string.IsNullOrWhiteSpace(authStatePath))
    {
        AnsiConsole.MarkupLine($"[red]{pathConfigKey} not configured for environment '{authEnvKey}' (or at the top level).[/]");
        return;
    }

    var loginTarget = isLegacy ? "forms login" : "SSO login";
    AnsiConsole.MarkupLine($"[cyan]Auth setup[/] — opening browser for {loginTarget}");
    AnsiConsole.MarkupLine($"[grey]Environment: {authEnvDisplay} ({authEnvKey})[/]");
    AnsiConsole.MarkupLine($"[grey]URL: {authBaseUrl}[/]");
    AnsiConsole.MarkupLine($"[grey]Storage state → {Markup.Escape(authStatePath)}[/]");
    AnsiConsole.MarkupLine("[grey]Complete the login (including 2FA if required), then the session will be saved automatically.[/]\n");

    using var pw = await Microsoft.Playwright.Playwright.CreateAsync();
    var authBrowser = await pw.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
    {
        Headless = false,
        SlowMo = 50,
        Args = ["--start-maximized"]
    });

    try
    {
        var authContext = await authBrowser.NewContextAsync(
            new Microsoft.Playwright.BrowserNewContextOptions { ViewportSize = Microsoft.Playwright.ViewportSize.NoViewport });
        var authPage = await authContext.NewPageAsync();

        var navigateUrl = isLegacy
            ? $"{authBaseUrl.TrimEnd('/')}{authConfig.LegacyWebUiLoginPath}"
            : authBaseUrl;
        await authPage.GotoAsync(navigateUrl,
            new Microsoft.Playwright.PageGotoOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle });

        AnsiConsole.MarkupLine("  Waiting for you to complete login (up to 3 minutes)...");
        AnsiConsole.MarkupLine("[grey]  Do NOT close the browser — it will close automatically once login is captured.[/]");

        try
        {
            // Poll page.Url instead of using WaitForURLAsync — the Playwright lifecycle
            // events (Load, DOMContentLoaded) can miss ASP.NET MVC redirect chains
            // (POST → 302 → GET) especially in an interactive auth-setup session.
            // Capture the URL we navigated to so we can detect when it changes
            var initialUrl = authPage.Url;
            var loginPath = authConfig.LegacyWebUiLoginPath.TrimStart('/');

            Func<string, bool> isLoggedIn = isLegacy
                ? (string.IsNullOrEmpty(loginPath)
                    // LoginPath not configured — detect any URL change from the initial page
                    ? url => !string.Equals(url, initialUrl, StringComparison.OrdinalIgnoreCase)
                             && !url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                    // LoginPath configured — detect URL no longer contains it
                    : url => !url.Contains(loginPath, StringComparison.OrdinalIgnoreCase))
                : url => url.StartsWith(authBaseUrl, StringComparison.OrdinalIgnoreCase)
                         && !url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                if (isLoggedIn(authPage.Url))
                    break;
                await Task.Delay(500);
            }

            if (!isLoggedIn(authPage.Url))
            {
                AnsiConsole.MarkupLine("\n[red]Timed out waiting for login to complete.[/]");
            }
            else
            {
                // Brief pause to let cookies settle after the redirect
                await Task.Delay(1000);

                // Save auth state
                var dir = Path.GetDirectoryName(authStatePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                await authContext.StorageStateAsync(
                    new Microsoft.Playwright.BrowserContextStorageStateOptions { Path = authStatePath });

                AnsiConsole.MarkupLine($"\n[green]Auth state saved[/] → {Markup.Escape(authStatePath)}");
                AnsiConsole.MarkupLine($"[grey]Valid for {authMaxAgeHours} hours. Recordings and test runs will use this session automatically.[/]");
            }
        }
        catch (Microsoft.Playwright.PlaywrightException ex) when (ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("\n[red]Browser was closed before auth state could be saved.[/]");
            AnsiConsole.MarkupLine("[yellow]Please run the command again and wait for the \"Auth state saved\" message before closing.[/]");
        }
    }
    finally
    {
        if (authBrowser.IsConnected) await authBrowser.CloseAsync();
    }

    return;
}

// ── Record-setup mode — capture reusable setup steps (e.g. login) for a test set ──
if (cli.RecordSetupMode)
{
    if (cli.ModuleId is null || cli.TestSetId is null)
    {
        AnsiConsole.MarkupLine("[red]--record-setup requires --module <id> and --testset <id>[/]");
        return;
    }

    // Load config to get base URL
    var setupConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()
        .GetSection("TestEnvironment")
        .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();
    var setupEnvResolver = new AiTestCrew.Agents.Environment.EnvironmentResolver(setupConfig);
    var setupEnvKey = setupEnvResolver.ResolveKey(cli.EnvironmentKey);

    var setupTargetType = cli.RecordTarget ?? "UI_Web_MVC";
    var setupBaseUrl = setupTargetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
        ? setupEnvResolver.ResolveBraveCloudUiUrl(setupEnvKey)
        : setupEnvResolver.ResolveLegacyWebUiUrl(setupEnvKey);

    if (string.IsNullOrWhiteSpace(setupBaseUrl))
    {
        var key = setupTargetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
        AnsiConsole.MarkupLine($"[red]Base URL not configured for environment '{setupEnvKey}'. Set '{key}' in the env block (or at the top level).[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[cyan]Recording setup steps[/] → {cli.ModuleId}/{cli.TestSetId}");
    AnsiConsole.MarkupLine($"[grey]Environment: {setupEnvResolver.ResolveDisplayName(setupEnvKey)} ({setupEnvKey})[/]");
    AnsiConsole.MarkupLine($"[grey]Target: {setupTargetType}  Base URL: {setupBaseUrl}[/]");
    AnsiConsole.MarkupLine("[grey]Perform your login/setup steps in the browser, then click Save & Stop.[/]\n");

    using var setupLoggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var setupRecLogger = setupLoggerFactory.CreateLogger("Recorder");

    // For Blazor targets, pass the saved auth state so the recorder starts authenticated
    var setupStorageState = setupTargetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
        ? setupEnvResolver.ResolveBraveCloudUiStorageStatePath(setupEnvKey) : null;
    if (!string.IsNullOrEmpty(setupStorageState) && !Path.IsPathRooted(setupStorageState))
        setupStorageState = Path.Combine(AppContext.BaseDirectory, setupStorageState);
    var setupRecorded = await PlaywrightRecorder.RecordAsync(setupBaseUrl, "setup", setupConfig, setupRecLogger, setupStorageState, setupTargetType);

    if (setupRecorded.Steps.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No steps were captured. Setup steps not saved.[/]");
        return;
    }

    // Resolve slugified IDs
    var setupModuleId  = SlugHelper.ToSlug(cli.ModuleId);
    var setupTestSetId = SlugHelper.ToSlug(cli.TestSetId);

    // Ensure module exists
    var setupModRepo = ResolveModuleRepo();
    if (!setupModRepo.Exists(setupModuleId))
        await setupModRepo.CreateAsync(cli.ModuleId);

    // Load or create the test set, then save setup steps into it
    var setupTsRepo = ResolveTsRepo();
    var setupTestSet = await setupTsRepo.LoadAsync(setupModuleId, setupTestSetId)
                       ?? await setupTsRepo.CreateEmptyAsync(setupModuleId, cli.TestSetId);

    setupTestSet.SetupStartUrl = setupRecorded.StartUrl;
    setupTestSet.SetupSteps = setupRecorded.Steps;
    await setupTsRepo.SaveAsync(setupTestSet, setupModuleId);

    // Print captured steps
    var setupTable = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]#[/]")
        .AddColumn("[bold]Action[/]")
        .AddColumn("[bold]Selector[/]")
        .AddColumn("[bold]Value[/]");

    for (int i = 0; i < setupRecorded.Steps.Count; i++)
    {
        var s = setupRecorded.Steps[i];
        var displayValue = s.Value is null ? "-" : (s.Value.Length > 40 ? s.Value[..40] + "…" : s.Value);
        setupTable.AddRow(
            (i + 1).ToString(),
            Markup.Escape(s.Action),
            Markup.Escape(s.Selector ?? "-"),
            Markup.Escape(displayValue)
        );
    }
    AnsiConsole.Write(setupTable);
    AnsiConsole.MarkupLine($"\n[green]Saved[/] {setupRecorded.Steps.Count} setup steps → {Markup.Escape(setupModuleId)}/{Markup.Escape(setupTestSetId)}");
    AnsiConsole.MarkupLine("[grey]These steps will run before every test case in this test set during replay.[/]");
    return;
}

// ── Migrate legacy test sets to module structure (idempotent) ──
await MigrationHelper.MigrateToModulesAsync(AppContext.BaseDirectory);
await MigrationHelper.MigrateToSchemaV2Async(AppContext.BaseDirectory);

// ── Build host ──
// Use AppContext.BaseDirectory (the binary output dir) as content root so that
// appsettings.json is found regardless of which directory dotnet run is called from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
});

// Bind TestEnvironmentConfig from the "TestEnvironment" section
var envConfig = builder.Configuration
    .GetSection("TestEnvironment")
    .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();

// Resolve storage state paths relative to the binary dir so they're consistent
// between CLI commands (--auth-setup, --record) and agent execution (--reuse).
if (!string.IsNullOrEmpty(envConfig.BraveCloudUiStorageStatePath)
    && !Path.IsPathRooted(envConfig.BraveCloudUiStorageStatePath))
{
    envConfig.BraveCloudUiStorageStatePath = Path.Combine(AppContext.BaseDirectory, envConfig.BraveCloudUiStorageStatePath);
}
if (!string.IsNullOrEmpty(envConfig.LegacyWebUiStorageStatePath)
    && !Path.IsPathRooted(envConfig.LegacyWebUiStorageStatePath))
{
    envConfig.LegacyWebUiStorageStatePath = Path.Combine(AppContext.BaseDirectory, envConfig.LegacyWebUiStorageStatePath);
}

builder.Services.AddSingleton(envConfig);

// Semantic Kernel — provider chosen by LlmProvider config value
var kernelBuilder = Kernel.CreateBuilder();

if (envConfig.LlmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
{
    // Use a direct IChatCompletionService wrapper to avoid MEA version-compatibility
    // issues between Anthropic.SDK and Microsoft.SemanticKernel.
    kernelBuilder.Services.AddSingleton<IChatCompletionService>(
        new AnthropicChatCompletionService(envConfig.LlmApiKey, envConfig.LlmModel));
}
else // OpenAI (default)
{
    kernelBuilder.AddOpenAIChatCompletion(envConfig.LlmModel, envConfig.LlmApiKey);
}

var kernel = kernelBuilder.Build();
builder.Services.AddSingleton(kernel);

// HttpClient factory + environment resolver + API target resolver + ApiTestAgent
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IEnvironmentResolver>(sp =>
    new AiTestCrew.Agents.Environment.EnvironmentResolver(sp.GetRequiredService<TestEnvironmentConfig>()));
builder.Services.AddSingleton<IApiTargetResolver>(sp => new ApiTargetResolver(
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetRequiredService<IEnvironmentResolver>()
));
builder.Services.AddSingleton<ApiTestAgent>(sp => new ApiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<ApiTestAgent>>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<IApiTargetResolver>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<ApiTestAgent>());

builder.Services.AddSingleton<LegacyWebUiTestAgent>(sp => new LegacyWebUiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<LegacyWebUiTestAgent>>(),
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<IEnvironmentResolver>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<LegacyWebUiTestAgent>());

builder.Services.AddSingleton<BraveCloudUiTestAgent>(sp => new BraveCloudUiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<BraveCloudUiTestAgent>>(),
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<IEnvironmentResolver>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<BraveCloudUiTestAgent>());

builder.Services.AddSingleton<WinFormsUiTestAgent>(sp => new WinFormsUiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<WinFormsUiTestAgent>>(),
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<IEnvironmentResolver>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<WinFormsUiTestAgent>());

// aseXML generation agent — template-driven AEMO B2B payload renderer
builder.Services.AddSingleton<TemplateRegistry>(sp => TemplateRegistry.LoadFrom(
    sp.GetRequiredService<TestEnvironmentConfig>().AseXml.TemplatesPath,
    sp.GetRequiredService<ILogger<TemplateRegistry>>()
));
builder.Services.AddSingleton<AseXmlGenerationAgent>(sp => new AseXmlGenerationAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<AseXmlGenerationAgent>>(),
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<TemplateRegistry>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<AseXmlGenerationAgent>());

// aseXML delivery agent — resolves endpoint from Bravo DB and uploads via SFTP/FTP
builder.Services.AddSingleton<IEndpointResolver>(sp => new BravoEndpointResolver(
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<IEnvironmentResolver>(),
    sp.GetRequiredService<ILogger<BravoEndpointResolver>>()
));
builder.Services.AddSingleton<DropTargetFactory>();
builder.Services.AddSingleton<AseXmlDeliveryAgent>(sp => new AseXmlDeliveryAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<AseXmlDeliveryAgent>>(),
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<TemplateRegistry>(),
    sp.GetRequiredService<IEndpointResolver>(),
    sp.GetRequiredService<DropTargetFactory>(),
    sp  // IServiceProvider — siblings resolved lazily to avoid DI recursion at construction
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<AseXmlDeliveryAgent>());

// Test set persistence + execution history + modules
if (!string.IsNullOrWhiteSpace(envConfig.ServerUrl))
{
    // Remote mode: call the WebApi over HTTP
    var remoteHttp = new AiTestCrew.Runner.RemoteRepositories.RemoteHttpClient(envConfig.ServerUrl, envConfig.ApiKey);
    builder.Services.AddSingleton<ITestSetRepository>(new AiTestCrew.Runner.RemoteRepositories.ApiClientTestSetRepository(remoteHttp));
    builder.Services.AddSingleton<IExecutionHistoryRepository>(new AiTestCrew.Runner.RemoteRepositories.ApiClientExecutionHistoryRepository(remoteHttp));
    builder.Services.AddSingleton<IModuleRepository>(new AiTestCrew.Runner.RemoteRepositories.ApiClientModuleRepository(remoteHttp));
    AnsiConsole.MarkupLine($"[grey]Remote mode → {envConfig.ServerUrl}[/]");
}
else if (envConfig.StorageProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var connFactory = new AiTestCrew.Agents.Persistence.Sqlite.SqliteConnectionFactory(envConfig.SqliteConnectionString);
    builder.Services.AddSingleton<ITestSetRepository>(new AiTestCrew.Agents.Persistence.Sqlite.SqliteTestSetRepository(connFactory));
    builder.Services.AddSingleton<IExecutionHistoryRepository>(new AiTestCrew.Agents.Persistence.Sqlite.SqliteExecutionHistoryRepository(connFactory, envConfig.MaxExecutionRunsPerTestSet));
    builder.Services.AddSingleton<IModuleRepository>(new AiTestCrew.Agents.Persistence.Sqlite.SqliteModuleRepository(connFactory));
}
else
{
    builder.Services.AddSingleton<ITestSetRepository>(new TestSetRepository(AppContext.BaseDirectory));
    builder.Services.AddSingleton<IExecutionHistoryRepository>(new ExecutionHistoryRepository(AppContext.BaseDirectory, envConfig.MaxExecutionRunsPerTestSet));
    builder.Services.AddSingleton<IModuleRepository>(new ModuleRepository(AppContext.BaseDirectory));
}

// Orchestrator (receives IEnumerable<ITestAgent> and ITestSetRepository from DI automatically)
builder.Services.AddSingleton(new AgentConcurrencyLimiter(envConfig.MaxParallelAgents));
builder.Services.AddSingleton<TestOrchestrator>();

// ── Logging ──
// All messages (debug and above) go to a timestamped log file.
// The console only shows AiTestCrew-namespace messages at Info+ so the
// Spectre spinner is not torn apart by System.Net.Http / Microsoft noise.
var logDir  = Path.Combine(AppContext.BaseDirectory, "logs");
var logFile = Path.Combine(logDir, $"testrun_{DateTime.Now:yyyyMMdd_HHmmss}.log");

builder.Logging.ClearProviders();

// File sink — captures everything
builder.Logging.AddProvider(new FileLoggerProvider(logFile));
builder.Logging.SetMinimumLevel(LogLevel.Trace);   // file gets it all

// Console sink — only our own code, at Info+ (suppress framework HTTP noise)
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine  = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
    "System",       LogLevel.None);
builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
    "Microsoft",    LogLevel.None);
builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
    "AiTestCrew",   envConfig.VerboseLogging ? LogLevel.Information : LogLevel.Warning);

var host = builder.Build();

// ── --list-environments: print configured customer environments and exit ──
if (cli.ListEnvironments)
{
    var envResolver = host.Services.GetRequiredService<IEnvironmentResolver>();
    var defaultKey = envResolver.ResolveKey(null);
    var keys = envResolver.ListKeys();

    var envTable = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]Key[/]")
        .AddColumn("[bold]Display Name[/]")
        .AddColumn("[bold]Default[/]");
    foreach (var k in keys)
    {
        var isDefault = string.Equals(k, defaultKey, StringComparison.OrdinalIgnoreCase);
        envTable.AddRow(k, envResolver.ResolveDisplayName(k), isDefault ? "✓" : "");
    }
    AnsiConsole.Write(envTable);
    AnsiConsole.MarkupLine($"[grey]{keys.Count} environment(s). Use --environment <key> to target one.[/]");
    return;
}

// ── --list-endpoints: print Bravo delivery endpoints and exit ──
if (cli.ListEndpoints)
{
    var resolver = host.Services.GetRequiredService<IEndpointResolver>();
    try
    {
        var codes = await resolver.ListCodesAsync();
        if (codes.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No endpoints found in mil.V2_MIL_EndPoint.[/]");
            return;
        }
        var epTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]EndPointCode[/]");
        foreach (var code in codes) epTable.AddRow(code);
        AnsiConsole.Write(epTable);
        AnsiConsole.MarkupLine($"[grey]{codes.Count} endpoint(s). Use --endpoint <code> to target one.[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to list endpoints:[/] {ex.Message.EscapeMarkup()}");
        AnsiConsole.MarkupLine("[grey]Check TestEnvironment.AseXml.BravoDb.ConnectionString in appsettings.json.[/]");
    }
    return;
}

// ── Agent mode — long-running worker that polls the server for queued jobs ──
if (cli.AgentMode)
{
    if (string.IsNullOrWhiteSpace(envConfig.ServerUrl))
    {
        AnsiConsole.MarkupLine("[red]--agent requires TestEnvironment.ServerUrl to be set in appsettings.json.[/]");
        AnsiConsole.MarkupLine("[grey]The agent connects to the central server to claim queued jobs.[/]");
        return;
    }

    var agentName = cli.AgentName
        ?? (string.IsNullOrWhiteSpace(envConfig.AgentName) ? Environment.MachineName : envConfig.AgentName);

    var caps = (cli.AgentCapabilities ?? envConfig.AgentCapabilities ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (caps.Length == 0)
        caps = new[] { "UI_Web_Blazor", "UI_Web_MVC", "UI_Desktop_WinForms" };

    var agentLogger = host.Services.GetRequiredService<ILogger<AiTestCrew.Runner.AgentMode.AgentRunner>>();
    var agentClient = new AiTestCrew.Runner.AgentMode.AgentClient(envConfig.ServerUrl, envConfig.ApiKey);
    var jobExecutor = new AiTestCrew.Runner.AgentMode.JobExecutor(
        host.Services.GetRequiredService<TestOrchestrator>());
    var agentRunner = new AiTestCrew.Runner.AgentMode.AgentRunner(
        agentClient, jobExecutor, envConfig, agentLogger, agentName, caps);

    using var shutdownCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        AnsiConsole.MarkupLine("\n[yellow]Ctrl+C received — draining current job and deregistering...[/]");
        shutdownCts.Cancel();
    };

    try
    {
        await agentRunner.RunAsync(shutdownCts.Token);
    }
    catch (OperationCanceledException) { /* graceful shutdown */ }
    return;
}

// ── Provider label ──
var providerLabel = envConfig.LlmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
    ? $"Claude ({envConfig.LlmModel})"
    : $"OpenAI ({envConfig.LlmModel})";
AnsiConsole.MarkupLine($"[grey]Powered by Semantic Kernel + {providerLabel}[/]");
AnsiConsole.MarkupLine($"[grey]Detailed log → {logFile}[/]\n");

// ── Objective ──
// In reuse mode the objective is loaded from the saved test set inside the orchestrator;
// no prompt needed here.
var objective = cli.Mode is RunMode.Reuse or RunMode.VerifyOnly
    ? string.Empty
    : cli.Objective ?? AnsiConsole.Ask<string>("[yellow]Enter test objective:[/]");

// ── Mode label ──
var modeLabel = cli.Mode switch
{
    RunMode.Reuse      => $"[cyan]REUSE[/] (test set: [bold]{cli.ReuseId}[/])",
    RunMode.VerifyOnly => $"[magenta]VERIFY-ONLY[/] (re-running verifications for: [bold]{cli.ReuseId ?? cli.TestSetId}[/])",
    RunMode.Rebaseline => "[yellow]REBASELINE[/] (regenerating test cases)",
    _                  => "[green]NORMAL[/] (generating new test cases)"
};
// In reuse mode, if --module is given but --testset isn't, the reuseId names
// the test set too (the common case). Auto-derive so users don't have to pass
// the same slug twice.
var effectiveTestSetId = cli.TestSetId;
if (cli.Mode is RunMode.Reuse or RunMode.VerifyOnly
    && cli.ModuleId is not null
    && string.IsNullOrEmpty(effectiveTestSetId)
    && !string.IsNullOrEmpty(cli.ReuseId))
{
    effectiveTestSetId = cli.ReuseId;
}

// ── VerifyOnly validation ──
if (cli.Mode == RunMode.VerifyOnly)
{
    if (string.IsNullOrEmpty(cli.ReuseId) && string.IsNullOrEmpty(effectiveTestSetId))
    {
        AnsiConsole.MarkupLine("[red]--verify-only requires --reuse <testSetId> (or --module + --testset)[/]");
        return;
    }
    if (string.IsNullOrEmpty(cli.ObjectiveId))
    {
        AnsiConsole.MarkupLine("[red]--verify-only requires --objective <idOrName> to identify the delivery test case[/]");
        return;
    }
}

AnsiConsole.MarkupLine($"[grey]Mode:[/] {modeLabel}");
if (cli.ModuleId is not null)
    AnsiConsole.MarkupLine($"[grey]Module:[/] {cli.ModuleId}  [grey]Test set:[/] {effectiveTestSetId}");
if (cli.ObjectiveId is not null)
    AnsiConsole.MarkupLine($"[grey]Objective filter:[/] {cli.ObjectiveId}");
if (cli.Mode is not RunMode.Reuse and not RunMode.VerifyOnly)
    AnsiConsole.MarkupLine($"[grey]Objective:[/] {objective}");
AnsiConsole.WriteLine();

// ── Run ──
var orchestrator = host.Services.GetRequiredService<TestOrchestrator>();
TestSuiteResult? suiteResult = null;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Running test suite...", async ctx =>
    {
        if (cli.Mode == RunMode.VerifyOnly)
            ctx.Status($"Re-running verifications for '{cli.ObjectiveId}'...");
        else if (cli.Mode == RunMode.Reuse)
            ctx.Status($"Loading saved test set '{cli.ReuseId}'...");
        else
            ctx.Status("Decomposing objective...");

        suiteResult = await orchestrator.RunAsync(objective, cli.Mode, cli.ReuseId,
            moduleId: cli.ModuleId, targetTestSetId: effectiveTestSetId,
            objectiveName: cli.ObjectiveName,
            objectiveId: cli.ObjectiveId,  // reuse-mode / verify-only filter to a single test case
            apiStackKey: cli.ApiStackKey, apiModule: cli.ApiModule,
            endpointCode: cli.EndpointCode,
            verificationWaitOverride: cli.Mode == RunMode.VerifyOnly ? cli.VerificationWait : null,
            environmentKey: cli.EnvironmentKey);
    });

// ── Results table ──
var table = new Table()
    .Border(TableBorder.Rounded)
    .AddColumn("[bold]Objective[/]")
    .AddColumn("[bold]Agent[/]")
    .AddColumn("[bold]Status[/]")
    .AddColumn("[bold]Steps[/]")
    .AddColumn("[bold]Summary[/]");

foreach (var r in suiteResult!.Results)
{
    var statusColor = r.Status switch
    {
        TestStatus.Passed => "green",
        TestStatus.Failed => "red",
        TestStatus.Error  => "yellow",
        _                 => "grey"
    };
    var summary = r.Summary.Length > 80 ? r.Summary[..80] + "…" : r.Summary;
    table.AddRow(
        r.ObjectiveId,
        r.AgentName,
        $"[{statusColor}]{r.Status}[/]",
        $"{r.PassedSteps}/{r.Steps.Count}",
        summary
    );
}

AnsiConsole.Write(table);

var overallColor = suiteResult.AllPassed ? "green" : "red";
var overallLabel = suiteResult.AllPassed ? "PASSED" : "FAILED";
AnsiConsole.MarkupLine(
    $"\n[bold]Overall:[/] [{overallColor}]{overallLabel}[/] " +
    $"({suiteResult.Passed}/{suiteResult.TotalObjectives} objectives) " +
    $"in {suiteResult.TotalDuration:mm\\:ss}");

AnsiConsole.MarkupLine($"\n[italic grey]{suiteResult.Summary}[/]");

// ── Test set save notification ──
if (cli.Mode is RunMode.Normal or RunMode.Rebaseline)
{
    var slug = SlugHelper.ToSlug(objective);
    var action = cli.Mode == RunMode.Rebaseline ? "Rebaselined" : "Saved";
    AnsiConsole.MarkupLine($"\n[grey]{action} test set → {slug}[/]");
    AnsiConsole.MarkupLine($"[grey]Re-run later:  dotnet run -- --reuse {slug}[/]");
    AnsiConsole.MarkupLine($"[grey]Regenerate:    dotnet run -- --rebaseline \"{objective}\"[/]");
}

// ── Local functions ──

static CliArgs ParseArgs(string[] args)
{
    if (args.Length == 0)
        return new CliArgs();

    // Scan for known flags
    var remaining = new List<string>();
    string? moduleId = null, testSetId = null, reuseId = null;
    string? createModuleName = null, createTestSetModuleId = null, createTestSetName = null;
    string? objectiveName = null, caseName = null, recordTarget = null;
    string? apiStackKey = null, apiModuleKey = null;
    string? endpointCode = null;
    string? environmentKey = null;
    bool listModules = false, recordMode = false, recordSetupMode = false, authSetupMode = false;
    bool listEndpoints = false, listEnvironments = false, migrateToSqlite = false;
    bool recordVerification = false;
    string? objectiveId = null, verificationName = null;
    int? verificationWait = null;
    int deliveryStepIndex = 0;
    bool agentMode = false;
    string? agentName = null, agentCapabilities = null;
    var mode = RunMode.Normal;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--list":
                mode = RunMode.List;
                break;
            case "--list-modules":
                listModules = true;
                break;
            case "--reuse" when i + 1 < args.Length:
                mode = RunMode.Reuse;
                reuseId = args[++i];
                break;
            case "--reuse":
                throw new ArgumentException("--reuse requires a <id> argument.");
            case "--rebaseline":
                mode = RunMode.Rebaseline;
                break;
            case "--verify-only":
                mode = RunMode.VerifyOnly;
                break;
            case "--module" when i + 1 < args.Length:
                moduleId = args[++i];
                break;
            case "--module":
                throw new ArgumentException("--module requires a <moduleId> argument.");
            case "--testset" when i + 1 < args.Length:
                testSetId = args[++i];
                break;
            case "--testset":
                throw new ArgumentException("--testset requires a <testSetId> argument.");
            case "--create-module" when i + 1 < args.Length:
                createModuleName = args[++i];
                break;
            case "--create-module":
                throw new ArgumentException("--create-module requires a \"Name\" argument.");
            case "--create-testset" when i + 2 < args.Length:
                createTestSetModuleId = args[++i];
                createTestSetName = args[++i];
                break;
            case "--create-testset":
                throw new ArgumentException("--create-testset requires <moduleId> \"Name\" arguments.");
            case "--obj-name" when i + 1 < args.Length:
                objectiveName = args[++i];
                break;
            case "--obj-name":
                throw new ArgumentException("--obj-name requires a \"Name\" argument.");
            case "--record":
                recordMode = true;
                break;
            case "--record-setup":
                recordSetupMode = true;
                break;
            case "--auth-setup":
                authSetupMode = true;
                break;
            case "--case-name" when i + 1 < args.Length:
                caseName = args[++i];
                break;
            case "--case-name":
                throw new ArgumentException("--case-name requires a \"Name\" argument.");
            case "--target" when i + 1 < args.Length:
                recordTarget = args[++i];
                break;
            case "--target":
                throw new ArgumentException("--target requires UI_Web_MVC or UI_Web_Blazor.");
            case "--stack" when i + 1 < args.Length:
                apiStackKey = args[++i];
                break;
            case "--stack":
                throw new ArgumentException("--stack requires a <stackKey> argument (e.g. bravecloud, legacy).");
            case "--api-module" when i + 1 < args.Length:
                apiModuleKey = args[++i];
                break;
            case "--api-module":
                throw new ArgumentException("--api-module requires a <moduleKey> argument (e.g. sdr, security).");
            case "--endpoint" when i + 1 < args.Length:
                endpointCode = args[++i];
                break;
            case "--endpoint":
                throw new ArgumentException("--endpoint requires an <EndPointCode> argument (e.g. GatewaySPARQ).");
            case "--list-endpoints":
                listEndpoints = true;
                break;
            case "--list-environments":
                listEnvironments = true;
                break;
            case "--environment" when i + 1 < args.Length:
                environmentKey = args[++i];
                break;
            case "--environment":
                throw new ArgumentException("--environment requires a <customerKey> argument (e.g. sumo-retail, ams-metering).");
            case "--migrate-to-sqlite":
                migrateToSqlite = true;
                break;
            case "--record-verification":
                recordVerification = true;
                break;
            case "--objective" when i + 1 < args.Length:
                // Context-dependent:
                //  * with --reuse: scopes the run to a single objective (test case) in the set
                //  * with --record-verification: the objective to attach the verification to
                objectiveId = args[++i];
                break;
            case "--objective":
                throw new ArgumentException("--objective requires an <objectiveId> argument.");
            case "--verification-name" when i + 1 < args.Length:
                verificationName = args[++i];
                break;
            case "--verification-name":
                throw new ArgumentException("--verification-name requires a \"<name>\" argument.");
            case "--wait" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], out var waitSecs))
                    throw new ArgumentException("--wait requires an integer number of seconds.");
                verificationWait = waitSecs;
                break;
            case "--wait":
                throw new ArgumentException("--wait requires an integer number of seconds.");
            case "--delivery-step-index" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], out var dsi))
                    throw new ArgumentException("--delivery-step-index requires an integer.");
                deliveryStepIndex = dsi;
                break;
            case "--delivery-step-index":
                throw new ArgumentException("--delivery-step-index requires an integer.");
            case "--agent":
                agentMode = true;
                break;
            case "--name" when i + 1 < args.Length:
                agentName = args[++i];
                break;
            case "--name":
                throw new ArgumentException("--name requires a \"<name>\" argument.");
            case "--capabilities" when i + 1 < args.Length:
                agentCapabilities = args[++i];
                break;
            case "--capabilities":
                throw new ArgumentException("--capabilities requires a comma-separated list.");
            default:
                remaining.Add(args[i]);
                break;
        }
    }

    var objective = remaining.Count > 0 ? string.Join(" ", remaining) : null;

    return new CliArgs
    {
        Mode = mode,
        Objective = objective,
        ObjectiveName = objectiveName,
        ReuseId = reuseId,
        ModuleId = moduleId,
        TestSetId = testSetId,
        ListModules = listModules,
        CreateModuleName = createModuleName,
        CreateTestSetModuleId = createTestSetModuleId,
        CreateTestSetName = createTestSetName,
        RecordMode = recordMode,
        RecordSetupMode = recordSetupMode,
        AuthSetupMode = authSetupMode,
        CaseName = caseName,
        RecordTarget = recordTarget,
        ApiStackKey = apiStackKey,
        ApiModule = apiModuleKey,
        EndpointCode = endpointCode,
        EnvironmentKey = environmentKey,
        ListEndpoints = listEndpoints,
        ListEnvironments = listEnvironments,
        MigrateToSqlite = migrateToSqlite,
        RecordVerification = recordVerification,
        ObjectiveId = objectiveId,
        VerificationName = verificationName,
        VerificationWait = verificationWait,
        DeliveryStepIndex = deliveryStepIndex,
        AgentMode = agentMode,
        AgentName = agentName,
        AgentCapabilities = agentCapabilities
    };
}

// ── Types ──

class CliArgs
{
    public RunMode Mode { get; init; } = RunMode.Normal;
    public string? Objective { get; init; }
    public string? ReuseId { get; init; }
    public string? ModuleId { get; init; }
    public string? TestSetId { get; init; }
    public bool ListModules { get; init; }
    public string? CreateModuleName { get; init; }
    public string? CreateTestSetModuleId { get; init; }
    public string? CreateTestSetName { get; init; }
    public string? ObjectiveName { get; init; }
    public bool RecordMode { get; init; }
    public bool RecordSetupMode { get; init; }
    public bool AuthSetupMode { get; init; }
    public string? CaseName { get; init; }
    public string? RecordTarget { get; init; }
    public string? ApiStackKey { get; init; }
    public string? ApiModule { get; init; }
    public string? EndpointCode { get; init; }
    public string? EnvironmentKey { get; init; }
    public bool ListEndpoints { get; init; }
    public bool ListEnvironments { get; init; }
    public bool MigrateToSqlite { get; init; }
    public bool RecordVerification { get; init; }

    // Agent mode (Phase 4)
    public bool AgentMode { get; init; }
    public string? AgentName { get; init; }
    public string? AgentCapabilities { get; init; }

    /// <summary>
    /// --objective &lt;id&gt;. Context-dependent:
    ///   * reuse mode: scope the run to a single objective within the test set
    ///   * --record-verification: the objective to attach the verification to
    /// </summary>
    public string? ObjectiveId { get; init; }

    public string? VerificationName { get; init; }
    public int? VerificationWait { get; init; }
    public int DeliveryStepIndex { get; init; }
}
