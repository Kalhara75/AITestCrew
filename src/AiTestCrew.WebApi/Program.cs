using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.AseXmlAgent.Templates;
using AiTestCrew.Agents.Auth;
using AiTestCrew.Agents.BraveCloudUiAgent;
using AiTestCrew.Agents.LegacyWebUiAgent;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.WinFormsUiAgent;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Services;
using AiTestCrew.Orchestrator;
using AiTestCrew.WebApi;
using AiTestCrew.WebApi.Endpoints;
using AiTestCrew.WebApi.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Shared config: look for Runner's appsettings.json so both projects use the same API keys.
//    Works with `dotnet run --project src/AiTestCrew.WebApi` from the repo root.
var runnerDir = Path.Combine(builder.Environment.ContentRootPath, "..", "AiTestCrew.Runner");
builder.Configuration.AddJsonFile(Path.Combine(runnerDir, "appsettings.json"), optional: true, reloadOnChange: false);

// External config directory (e.g. Docker volume-mounted /config)
// Allows overriding config without rebuilding the image.
var externalConfig = Environment.GetEnvironmentVariable("AITESTCREW_CONFIG_PATH") ?? "C:/config/appsettings.json";
builder.Configuration.AddJsonFile(externalConfig, optional: true, reloadOnChange: false);

// Environment variable overrides (e.g. AITESTCREW_TestEnvironment__LlmApiKey)
builder.Configuration.AddEnvironmentVariables("AITESTCREW_");

// ── TestEnvironmentConfig ──
var envConfig = builder.Configuration
    .GetSection("TestEnvironment")
    .Get<TestEnvironmentConfig>() ?? new TestEnvironmentConfig();
builder.Services.AddSingleton(envConfig);

// ── Semantic Kernel + LLM provider ──
var kernelBuilder = Kernel.CreateBuilder();

if (envConfig.LlmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
{
    kernelBuilder.Services.AddSingleton<IChatCompletionService>(
        new AnthropicChatCompletionService(envConfig.LlmApiKey, envConfig.LlmModel));
}
else
{
    kernelBuilder.AddOpenAIChatCompletion(envConfig.LlmModel, envConfig.LlmApiKey);
}

var kernel = kernelBuilder.Build();
builder.Services.AddSingleton(kernel);

// ── HttpClient + API target resolver + API Agent ──
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
    sp  // IServiceProvider — siblings resolved lazily to avoid DI recursion
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<AseXmlDeliveryAgent>());

// ── Persistence — share the same data directory as the Runner ──
var runnerBinDir = Path.GetFullPath(Path.Combine(runnerDir, "bin", "Debug", "net8.0-windows"));
var dataDir = Directory.Exists(Path.Combine(runnerBinDir, "testsets"))
              || Directory.Exists(Path.Combine(runnerBinDir, "modules"))
    ? runnerBinDir
    : AppContext.BaseDirectory;

// Resolve storage state paths relative to the shared data directory so that
// auth state saved by the Runner CLI is found by the WebApi (and vice versa).
if (!string.IsNullOrEmpty(envConfig.BraveCloudUiStorageStatePath)
    && !Path.IsPathRooted(envConfig.BraveCloudUiStorageStatePath))
{
    envConfig.BraveCloudUiStorageStatePath = Path.Combine(dataDir, envConfig.BraveCloudUiStorageStatePath);
}
if (!string.IsNullOrEmpty(envConfig.LegacyWebUiStorageStatePath)
    && !Path.IsPathRooted(envConfig.LegacyWebUiStorageStatePath))
{
    envConfig.LegacyWebUiStorageStatePath = Path.Combine(dataDir, envConfig.LegacyWebUiStorageStatePath);
}

if (envConfig.StorageProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var connFactory = new AiTestCrew.Agents.Persistence.Sqlite.SqliteConnectionFactory(envConfig.SqliteConnectionString);
    builder.Services.AddSingleton(connFactory);
    builder.Services.AddSingleton<ITestSetRepository>(new AiTestCrew.Agents.Persistence.Sqlite.SqliteTestSetRepository(connFactory));
    builder.Services.AddSingleton<IExecutionHistoryRepository>(new AiTestCrew.Agents.Persistence.Sqlite.SqliteExecutionHistoryRepository(connFactory, envConfig.MaxExecutionRunsPerTestSet));
    builder.Services.AddSingleton<IModuleRepository>(new AiTestCrew.Agents.Persistence.Sqlite.SqliteModuleRepository(connFactory));
    builder.Services.AddSingleton<IUserRepository>(new AiTestCrew.Agents.Persistence.Sqlite.SqliteUserRepository(connFactory));
}
else
{
    builder.Services.AddSingleton<ITestSetRepository>(new TestSetRepository(dataDir));
    builder.Services.AddSingleton<IExecutionHistoryRepository>(new ExecutionHistoryRepository(dataDir, envConfig.MaxExecutionRunsPerTestSet));
    builder.Services.AddSingleton<IModuleRepository>(new ModuleRepository(dataDir));
}

// ── Orchestrator ──
builder.Services.AddSingleton(new AgentConcurrencyLimiter(envConfig.MaxParallelAgents));
builder.Services.AddSingleton<TestOrchestrator>();

// ── Run tracker (in-memory active runs) ──
builder.Services.AddSingleton<IRunTracker, RunTracker>();
builder.Services.AddSingleton<IModuleRunTracker, ModuleRunTracker>();

// ── CORS ──
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = envConfig.CorsOrigins is { Length: > 0 }
            ? envConfig.CorsOrigins
            : new[] { "http://localhost:5173", "http://localhost:3000" };

        if (origins.Length == 1 && origins[0] == "*")
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        else
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

// ── JSON serialisation ──
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseCors();
app.UseMiddleware<AiTestCrew.WebApi.Middleware.ApiKeyAuthMiddleware>();

// ── Serve the React SPA from wwwroot (production build) ──
app.UseDefaultFiles();    // serves index.html for "/"
app.UseStaticFiles();     // serves JS/CSS/assets from wwwroot

// ── Migrate legacy test sets to module structure ──
await MigrationHelper.MigrateToModulesAsync(dataDir);
await MigrationHelper.MigrateToSchemaV2Async(dataDir);

// ── Serve Playwright screenshots as static files ──
if (!string.IsNullOrEmpty(envConfig.PlaywrightScreenshotDir))
{
    var screenshotDir = Path.GetFullPath(envConfig.PlaywrightScreenshotDir);
    if (!Directory.Exists(screenshotDir))
        Directory.CreateDirectory(screenshotDir);

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(screenshotDir),
        RequestPath = "/screenshots"
    });
}

// ── Map endpoints ──
app.MapGroup("/api/modules").MapModuleEndpoints();
app.MapGroup("/api/testsets").MapTestSetEndpoints();
app.MapGroup("/api/runs").MapRunEndpoints();
if (envConfig.StorageProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    app.MapGroup("/api/users").MapUserEndpoints();

// ── Execution history (Runner API client) ──
app.MapPost("/api/executions", async (PersistedExecutionRun run, IExecutionHistoryRepository historyRepo) =>
{
    await historyRepo.SaveAsync(run);
    return Results.Ok(new { saved = true, runId = run.RunId });
});

// ── Health check ──
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

// ── Auth status (always public — tells the frontend whether login is required) ──
app.MapGet("/api/auth/status", (IServiceProvider sp) =>
{
    var authEnabled = sp.GetService<IUserRepository>() is not null;
    return Results.Ok(new { authEnabled });
});

// ── API stack discovery — exposes configured stacks/modules to the UI ──
app.MapGet("/api/config/api-stacks", (TestEnvironmentConfig cfg) =>
{
    var stacks = cfg.ApiStacks.ToDictionary(
        kvp => kvp.Key,
        kvp => new
        {
            baseUrl = kvp.Value.BaseUrl,
            modules = kvp.Value.Modules.ToDictionary(
                m => m.Key,
                m => new { m.Value.Name, m.Value.PathPrefix })
        });

    return Results.Ok(new
    {
        stacks,
        defaultStack = cfg.DefaultApiStack,
        defaultModule = cfg.DefaultApiModule
    });
});

// ── SPA fallback — serve index.html for client-side routes ──
app.MapFallbackToFile("index.html");

var listenUrl = !string.IsNullOrWhiteSpace(envConfig.ListenUrl)
    ? envConfig.ListenUrl
    : "http://localhost:5050";
foreach (var url in listenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    app.Urls.Add(url);
app.Run();
