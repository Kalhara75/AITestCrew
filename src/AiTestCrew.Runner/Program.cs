using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Spectre.Console;
using AiTestCrew.Agents.ApiAgent;
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

// ── Short-circuit commands that don't need the LLM host ──
if (cli.Mode == RunMode.List)
{
    var repo = new TestSetRepository(AppContext.BaseDirectory);
    var histRepo = new ExecutionHistoryRepository(AppContext.BaseDirectory);
    var sets = repo.ListAll();

    if (sets.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No saved test sets found.[/]");
        AnsiConsole.MarkupLine($"[grey]Test sets directory: {repo.Directory}[/]");
        return;
    }

    var listTable = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]ID[/]")
        .AddColumn("[bold]Module[/]")
        .AddColumn("[bold]Objective[/]")
        .AddColumn("[bold]Tasks[/]")
        .AddColumn("[bold]Cases[/]")
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
            s.Tasks.Count.ToString(),
            s.Tasks.Sum(t => t.TestCases.Count).ToString(),
            s.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            s.LastRunAt == default ? "-" : s.LastRunAt.ToString("yyyy-MM-dd HH:mm"),
            histRepo.CountRuns(s.Id).ToString()
        );
    }

    AnsiConsole.Write(listTable);
    AnsiConsole.MarkupLine($"\n[grey]Test sets directory: {repo.Directory}[/]");
    AnsiConsole.MarkupLine("[grey]Re-run a saved set:  dotnet run -- --reuse <id>[/]");
    AnsiConsole.MarkupLine("[grey]Regenerate & resave: dotnet run -- --rebaseline \"<objective>\"[/]");
    return;
}

if (cli.ListModules)
{
    var moduleRepo = new ModuleRepository(AppContext.BaseDirectory);
    var tsRepo = new TestSetRepository(AppContext.BaseDirectory);
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
            testSets.Sum(ts => ts.Tasks.Sum(t => t.TestCases.Count)).ToString(),
            m.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        );
    }

    AnsiConsole.Write(moduleTable);
    return;
}

if (cli.CreateModuleName is not null)
{
    var moduleRepo = new ModuleRepository(AppContext.BaseDirectory);
    var module = await moduleRepo.CreateAsync(cli.CreateModuleName);
    AnsiConsole.MarkupLine($"[green]Module created:[/] {module.Id} ({module.Name})");
    AnsiConsole.MarkupLine($"[grey]Create a test set: dotnet run -- --create-testset {module.Id} \"Test Set Name\"[/]");
    return;
}

if (cli.CreateTestSetModuleId is not null && cli.CreateTestSetName is not null)
{
    var moduleRepo = new ModuleRepository(AppContext.BaseDirectory);
    if (!moduleRepo.Exists(cli.CreateTestSetModuleId))
    {
        AnsiConsole.MarkupLine($"[red]Module '{cli.CreateTestSetModuleId}' not found.[/]");
        return;
    }
    var tsRepo = new TestSetRepository(AppContext.BaseDirectory);
    var testSet = await tsRepo.CreateEmptyAsync(cli.CreateTestSetModuleId, cli.CreateTestSetName);
    AnsiConsole.MarkupLine($"[green]Test set created:[/] {cli.CreateTestSetModuleId}/{testSet.Id} ({testSet.Name})");
    AnsiConsole.MarkupLine($"[grey]Run objective: dotnet run -- --module {cli.CreateTestSetModuleId} --testset {testSet.Id} \"<objective>\"[/]");
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

    var targetType = cli.RecordTarget ?? "UI_Web_MVC";
    var isDesktop = targetType.Equals("UI_Desktop_WinForms", StringComparison.OrdinalIgnoreCase);

    using var loggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var recLogger = loggerFactory.CreateLogger("Recorder");

    // Resolve slugified IDs so the saved file matches what the WebApi/UI expects
    var moduleId  = SlugHelper.ToSlug(cli.ModuleId);
    var testSetId = SlugHelper.ToSlug(cli.TestSetId);

    // Ensure the module directory and manifest exist (idempotent)
    var modRepo = new ModuleRepository(AppContext.BaseDirectory);
    if (!modRepo.Exists(moduleId))
        await modRepo.CreateAsync(cli.ModuleId);

    // Save into the test set
    var tsRepo = new TestSetRepository(AppContext.BaseDirectory);
    var testSet = await tsRepo.LoadAsync(moduleId, testSetId)
                  ?? await tsRepo.CreateEmptyAsync(moduleId, cli.TestSetId);

    var objectiveId = $"recorded-{SlugHelper.ToSlug(cli.CaseName)}";

    if (isDesktop)
    {
        // ── Desktop recording path ──
        if (string.IsNullOrWhiteSpace(recordConfig.WinFormsAppPath))
        {
            AnsiConsole.MarkupLine("[red]Application path not configured. Set 'WinFormsAppPath' in appsettings.json.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Recording[/] → {cli.ModuleId}/{cli.TestSetId}  case: [bold]{cli.CaseName}[/]");
        AnsiConsole.MarkupLine($"[grey]Target: {targetType}  App: {recordConfig.WinFormsAppPath}[/]\n");

        var desktopRecorded = await DesktopRecorder.RecordAsync(
            recordConfig.WinFormsAppPath, recordConfig.WinFormsAppArgs,
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
        ? recordConfig.BraveCloudUiUrl
        : recordConfig.LegacyWebUiUrl;

    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        var key = targetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
        AnsiConsole.MarkupLine($"[red]Base URL not configured. Set '{key}' in appsettings.json.[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[cyan]Recording[/] → {cli.ModuleId}/{cli.TestSetId}  case: [bold]{cli.CaseName}[/]");
    AnsiConsole.MarkupLine($"[grey]Target: {targetType}  Base URL: {baseUrl}[/]\n");

    // For Blazor targets, pass the saved auth state so the recorder starts authenticated
    var recordStorageState = targetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
        ? recordConfig.BraveCloudUiStorageStatePath : null;
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

// ── Auth-setup mode — perform SSO login (with optional manual 2FA) and save browser auth state ──
if (cli.AuthSetupMode)
{
    var authConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()
        .GetSection("TestEnvironment")
        .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();

    var authTargetType = cli.RecordTarget ?? "UI_Web_Blazor";
    var isLegacy = authTargetType.Equals("UI_Web_MVC", StringComparison.OrdinalIgnoreCase);
    var authBaseUrl = isLegacy ? authConfig.LegacyWebUiUrl : authConfig.BraveCloudUiUrl;
    // Resolve relative path against bin dir so the file lands where the agent looks for it
    var authStatePath = isLegacy ? authConfig.LegacyWebUiStorageStatePath : authConfig.BraveCloudUiStorageStatePath;
    var authMaxAgeHours = isLegacy ? authConfig.LegacyWebUiStorageStateMaxAgeHours : authConfig.BraveCloudUiStorageStateMaxAgeHours;
    if (!string.IsNullOrEmpty(authStatePath) && !Path.IsPathRooted(authStatePath))
        authStatePath = Path.Combine(AppContext.BaseDirectory, authStatePath);

    var urlConfigKey = isLegacy ? "LegacyWebUiUrl" : "BraveCloudUiUrl";
    var pathConfigKey = isLegacy ? "LegacyWebUiStorageStatePath" : "BraveCloudUiStorageStatePath";
    if (string.IsNullOrWhiteSpace(authBaseUrl))
    {
        AnsiConsole.MarkupLine($"[red]{urlConfigKey} not configured in appsettings.json.[/]");
        return;
    }
    if (string.IsNullOrWhiteSpace(authStatePath))
    {
        AnsiConsole.MarkupLine($"[red]{pathConfigKey} not configured in appsettings.json.[/]");
        return;
    }

    var loginTarget = isLegacy ? "forms login" : "SSO login";
    AnsiConsole.MarkupLine($"[cyan]Auth setup[/] — opening browser for {loginTarget}");
    AnsiConsole.MarkupLine($"[grey]URL: {authBaseUrl}[/]");
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

    var setupTargetType = cli.RecordTarget ?? "UI_Web_MVC";
    var setupBaseUrl = setupTargetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
        ? setupConfig.BraveCloudUiUrl
        : setupConfig.LegacyWebUiUrl;

    if (string.IsNullOrWhiteSpace(setupBaseUrl))
    {
        var key = setupTargetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
            ? "BraveCloudUiUrl" : "LegacyWebUiUrl";
        AnsiConsole.MarkupLine($"[red]Base URL not configured. Set '{key}' in appsettings.json.[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[cyan]Recording setup steps[/] → {cli.ModuleId}/{cli.TestSetId}");
    AnsiConsole.MarkupLine($"[grey]Target: {setupTargetType}  Base URL: {setupBaseUrl}[/]");
    AnsiConsole.MarkupLine("[grey]Perform your login/setup steps in the browser, then click Save & Stop.[/]\n");

    using var setupLoggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var setupRecLogger = setupLoggerFactory.CreateLogger("Recorder");

    // For Blazor targets, pass the saved auth state so the recorder starts authenticated
    var setupStorageState = setupTargetType.Equals("UI_Web_Blazor", StringComparison.OrdinalIgnoreCase)
        ? setupConfig.BraveCloudUiStorageStatePath : null;
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
    var setupModRepo = new ModuleRepository(AppContext.BaseDirectory);
    if (!setupModRepo.Exists(setupModuleId))
        await setupModRepo.CreateAsync(cli.ModuleId);

    // Load or create the test set, then save setup steps into it
    var setupTsRepo = new TestSetRepository(AppContext.BaseDirectory);
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

// HttpClient factory + API target resolver + ApiTestAgent
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IApiTargetResolver>(sp => new ApiTargetResolver(
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<ILoggerFactory>()
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
    sp.GetRequiredService<TestEnvironmentConfig>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<LegacyWebUiTestAgent>());

builder.Services.AddSingleton<BraveCloudUiTestAgent>(sp => new BraveCloudUiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<BraveCloudUiTestAgent>>(),
    sp.GetRequiredService<TestEnvironmentConfig>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<BraveCloudUiTestAgent>());

builder.Services.AddSingleton<WinFormsUiTestAgent>(sp => new WinFormsUiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<WinFormsUiTestAgent>>(),
    sp.GetRequiredService<TestEnvironmentConfig>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<WinFormsUiTestAgent>());

// Test set persistence + execution history + modules
builder.Services.AddSingleton(new TestSetRepository(AppContext.BaseDirectory));
builder.Services.AddSingleton(new ExecutionHistoryRepository(AppContext.BaseDirectory, envConfig.MaxExecutionRunsPerTestSet));
builder.Services.AddSingleton(new ModuleRepository(AppContext.BaseDirectory));

// Orchestrator (receives IEnumerable<ITestAgent> and TestSetRepository from DI automatically)
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

// ── Provider label ──
var providerLabel = envConfig.LlmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
    ? $"Claude ({envConfig.LlmModel})"
    : $"OpenAI ({envConfig.LlmModel})";
AnsiConsole.MarkupLine($"[grey]Powered by Semantic Kernel + {providerLabel}[/]");
AnsiConsole.MarkupLine($"[grey]Detailed log → {logFile}[/]\n");

// ── Objective ──
// In reuse mode the objective is loaded from the saved test set inside the orchestrator;
// no prompt needed here.
var objective = cli.Mode == RunMode.Reuse
    ? string.Empty
    : cli.Objective ?? AnsiConsole.Ask<string>("[yellow]Enter test objective:[/]");

// ── Mode label ──
var modeLabel = cli.Mode switch
{
    RunMode.Reuse      => $"[cyan]REUSE[/] (test set: [bold]{cli.ReuseId}[/])",
    RunMode.Rebaseline => "[yellow]REBASELINE[/] (regenerating test cases)",
    _                  => "[green]NORMAL[/] (generating new test cases)"
};
AnsiConsole.MarkupLine($"[grey]Mode:[/] {modeLabel}");
if (cli.ModuleId is not null)
    AnsiConsole.MarkupLine($"[grey]Module:[/] {cli.ModuleId}  [grey]Test set:[/] {cli.TestSetId}");
if (cli.Mode != RunMode.Reuse)
    AnsiConsole.MarkupLine($"[grey]Objective:[/] {objective}");
AnsiConsole.WriteLine();

// ── Run ──
var orchestrator = host.Services.GetRequiredService<TestOrchestrator>();
TestSuiteResult? suiteResult = null;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Running test suite...", async ctx =>
    {
        if (cli.Mode == RunMode.Reuse)
            ctx.Status($"Loading saved test set '{cli.ReuseId}'...");
        else
            ctx.Status("Decomposing objective...");

        suiteResult = await orchestrator.RunAsync(objective, cli.Mode, cli.ReuseId,
            moduleId: cli.ModuleId, targetTestSetId: cli.TestSetId,
            objectiveName: cli.ObjectiveName,
            apiStackKey: cli.ApiStackKey, apiModule: cli.ApiModule);
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
    var repoForDisplay = host.Services.GetRequiredService<TestSetRepository>();
    var slug = TestSetRepository.SlugFromObjective(objective);
    var savedPath = repoForDisplay.FilePath(slug);
    if (File.Exists(savedPath))
    {
        var action = cli.Mode == RunMode.Rebaseline ? "Rebaselined" : "Saved";
        AnsiConsole.MarkupLine($"\n[grey]{action} test set → {savedPath}[/]");
        AnsiConsole.MarkupLine($"[grey]Re-run later:  dotnet run -- --reuse {slug}[/]");
        AnsiConsole.MarkupLine($"[grey]Regenerate:    dotnet run -- --rebaseline \"{objective}\"[/]");
    }
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
    bool listModules = false, recordMode = false, recordSetupMode = false, authSetupMode = false;
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
        ApiModule = apiModuleKey
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
}
