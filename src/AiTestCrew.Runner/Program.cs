using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Spectre.Console;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
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
            s.RunCount.ToString()
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

// ── Migrate legacy test sets to module structure (idempotent) ──
await MigrationHelper.MigrateToModulesAsync(AppContext.BaseDirectory);

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

// HttpClient factory + ApiTestAgent (explicit factory so HttpClient is wired cleanly)
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ApiTestAgent>(sp => new ApiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<ApiTestAgent>>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<TestEnvironmentConfig>()
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<ApiTestAgent>());

// Test set persistence + execution history + modules
builder.Services.AddSingleton(new TestSetRepository(AppContext.BaseDirectory));
builder.Services.AddSingleton(new ExecutionHistoryRepository(AppContext.BaseDirectory));
builder.Services.AddSingleton(new ModuleRepository(AppContext.BaseDirectory));

// Orchestrator (receives IEnumerable<ITestAgent> and TestSetRepository from DI automatically)
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
            objectiveName: cli.ObjectiveName);
    });

// ── Results table ──
var table = new Table()
    .Border(TableBorder.Rounded)
    .AddColumn("[bold]Task ID[/]")
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
        r.TaskId,
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
    $"({suiteResult.Passed}/{suiteResult.TotalTasks} tasks) " +
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
    string? objectiveName = null;
    bool listModules = false;
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
        CreateTestSetName = createTestSetName
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
}
