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
using AiTestCrew.Agents.Teardown;
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

// Pre-host helper for --record / --record-setup / --record-verification / --auth-setup.
AiTestCrew.Agents.Recording.RecordingService CreateRecordingService(ILoggerFactory lf)
{
    var envResolver = new AiTestCrew.Agents.Environment.EnvironmentResolver(quickConfig);
    return new AiTestCrew.Agents.Recording.RecordingService(
        quickConfig, envResolver,
        ResolveModuleRepo(), ResolveTsRepo(), ResolveHistRepo(),
        lf, lf.CreateLogger<AiTestCrew.Agents.Recording.RecordingService>());
}

void PrintRecordingResult(AiTestCrew.Agents.Recording.RecordingResult r)
{
    if (!r.Success)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(r.Error ?? r.Summary)}[/]");
        return;
    }
    AnsiConsole.MarkupLine($"[green]{Markup.Escape(r.Summary)}[/]");
    if (r.Steps is not { Count: > 0 }) return;

    var hasSelector = r.Steps.Any(s => !string.IsNullOrEmpty(s.Selector));
    var hasDesktop  = r.Steps.Any(s => !string.IsNullOrEmpty(s.AutomationId) || !string.IsNullOrEmpty(s.Name));
    var t = new Table().Border(TableBorder.Rounded).AddColumn("[bold]#[/]").AddColumn("[bold]Action[/]");
    if (hasSelector) t.AddColumn("[bold]Selector[/]");
    if (hasDesktop)  { t.AddColumn("[bold]AutomationId[/]"); t.AddColumn("[bold]Name[/]"); }
    t.AddColumn("[bold]Value[/]");
    for (int i = 0; i < r.Steps.Count; i++)
    {
        var s = r.Steps[i];
        var v = s.Value is null ? "-" : (s.Value.Length > 40 ? s.Value[..40] + "..." : s.Value);
        var row = new List<string> { (i + 1).ToString(), Markup.Escape(s.Action) };
        if (hasSelector) row.Add(Markup.Escape(s.Selector ?? "-"));
        if (hasDesktop)  { row.Add(Markup.Escape(s.AutomationId ?? "-")); row.Add(Markup.Escape(s.Name ?? "-")); }
        row.Add(Markup.Escape(v));
        t.AddRow(row.ToArray());
    }
    AnsiConsole.Write(t);
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

// ── Record mode — human-driven capture, delegates to RecordingService ──
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
    var recTarget = cli.RecordTarget ?? "UI_Web_MVC";
    AnsiConsole.MarkupLine($"[cyan]Recording[/] → {cli.ModuleId}/{cli.TestSetId}  case: [bold]{cli.CaseName}[/]");
    AnsiConsole.MarkupLine($"[grey]Target: {recTarget}[/]\n");

    using var recLoggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var recSvc = CreateRecordingService(recLoggerFactory);
    var recResult = await recSvc.RecordCaseAsync(new AiTestCrew.Agents.Recording.RecordCaseRequest(
        ModuleId: cli.ModuleId!,
        TestSetId: cli.TestSetId!,
        CaseName: cli.CaseName!,
        Target: recTarget,
        EnvironmentKey: cli.EnvironmentKey));
    PrintRecordingResult(recResult);
    if (recResult.Success)
        AnsiConsole.MarkupLine($"[grey]Replay: dotnet run -- --reuse {Markup.Escape(SlugHelper.ToSlug(cli.TestSetId))}[/]");
    return;
}

// ── Record-verification mode — delegates to RecordingService ──
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
    AnsiConsole.MarkupLine($"[cyan]Recording verification[/] for {Markup.Escape(cli.ObjectiveId!)} → target [bold]{verifyTarget}[/]\n");

    using var verifyLoggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var verifySvc = CreateRecordingService(verifyLoggerFactory);
    var verifyResult = await verifySvc.RecordVerificationAsync(new AiTestCrew.Agents.Recording.RecordVerificationRequest(
        ModuleId: cli.ModuleId!,
        TestSetId: cli.TestSetId!,
        ObjectiveId: cli.ObjectiveId!,
        VerificationName: cli.VerificationName!,
        Target: verifyTarget,
        WaitBeforeSeconds: cli.VerificationWait ?? 0,
        DeliveryStepIndex: cli.DeliveryStepIndex,
        EnvironmentKey: cli.EnvironmentKey));
    PrintRecordingResult(verifyResult);
    return;
}

// ── Auth-setup mode — delegates to RecordingService ──
if (cli.AuthSetupMode)
{
    var authTarget = cli.RecordTarget ?? "UI_Web_Blazor";
    AnsiConsole.MarkupLine($"[cyan]Auth setup[/] — opening browser for {authTarget}");
    AnsiConsole.MarkupLine("[grey]Complete the login (including 2FA if required). The session will be saved automatically.[/]\n");

    using var authLoggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var authSvc = CreateRecordingService(authLoggerFactory);
    var authResult = await authSvc.AuthSetupAsync(new AiTestCrew.Agents.Recording.AuthSetupRequest(
        Target: authTarget,
        EnvironmentKey: cli.EnvironmentKey));
    PrintRecordingResult(authResult);
    return;
}

// ── Record-setup mode — delegates to RecordingService ──
if (cli.RecordSetupMode)
{
    if (cli.ModuleId is null || cli.TestSetId is null)
    {
        AnsiConsole.MarkupLine("[red]--record-setup requires --module <id> and --testset <id>[/]");
        return;
    }
    var setupTarget = cli.RecordTarget ?? "UI_Web_MVC";
    AnsiConsole.MarkupLine($"[cyan]Recording setup steps[/] → {cli.ModuleId}/{cli.TestSetId}");
    AnsiConsole.MarkupLine($"[grey]Target: {setupTarget}[/]");
    AnsiConsole.MarkupLine("[grey]Perform your login/setup steps in the browser, then click Save & Stop.[/]\n");

    using var setupLoggerFactory = LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
         .AddFilter("AiTestCrew", LogLevel.Information));
    var setupSvc = CreateRecordingService(setupLoggerFactory);
    var setupResult = await setupSvc.RecordSetupAsync(new AiTestCrew.Agents.Recording.RecordSetupRequest(
        ModuleId: cli.ModuleId!,
        TestSetId: cli.TestSetId!,
        Target: setupTarget,
        EnvironmentKey: cli.EnvironmentKey));
    PrintRecordingResult(setupResult);
    if (setupResult.Success)
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

// Teardown executor — runs user-defined SQL DELETE statements before each
// objective when the test set defines TeardownSteps and the env opts in.
builder.Services.AddSingleton<ITeardownExecutor>(sp => new BravoTeardownExecutor(
    sp.GetRequiredService<IEnvironmentResolver>(),
    sp.GetRequiredService<ILogger<BravoTeardownExecutor>>()
));

// Orchestrator (receives IEnumerable<ITestAgent> and ITestSetRepository from DI automatically)
builder.Services.AddSingleton(new AgentConcurrencyLimiter(envConfig.MaxParallelAgents));
builder.Services.AddSingleton<TestOrchestrator>();

// Shared recording service — used by CLI flows (--record etc.) and the agent queue
builder.Services.AddSingleton<AiTestCrew.Agents.Recording.IRecordingService, AiTestCrew.Agents.Recording.RecordingService>();

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
        host.Services.GetRequiredService<TestOrchestrator>(),
        host.Services.GetRequiredService<AiTestCrew.Agents.Recording.IRecordingService>());
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
            environmentKey: cli.EnvironmentKey,
            teardownDryRun: cli.TeardownDryRun,
            skipTeardown: cli.SkipTeardown);
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
    bool teardownDryRun = false, skipTeardown = false;
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
            case "--teardown-dry-run":
                teardownDryRun = true;
                break;
            case "--skip-teardown":
                skipTeardown = true;
                break;
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
        AgentCapabilities = agentCapabilities,
        TeardownDryRun = teardownDryRun,
        SkipTeardown = skipTeardown
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

    /// <summary>--teardown-dry-run: log substituted teardown SQL but don't execute.</summary>
    public bool TeardownDryRun { get; init; }

    /// <summary>--skip-teardown: bypass test-set teardown entirely for this run.</summary>
    public bool SkipTeardown { get; init; }
}
