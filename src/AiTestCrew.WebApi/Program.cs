using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.Auth;
using AiTestCrew.Agents.BraveCloudUiAgent;
using AiTestCrew.Agents.LegacyWebUiAgent;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Orchestrator;
using AiTestCrew.WebApi;
using AiTestCrew.WebApi.Endpoints;
using AiTestCrew.WebApi.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Shared config: look for Runner's appsettings.json so both projects use the same API keys.
//    Works with `dotnet run --project src/AiTestCrew.WebApi` from the repo root.
var runnerDir = Path.Combine(builder.Environment.ContentRootPath, "..", "AiTestCrew.Runner");
builder.Configuration.AddJsonFile(Path.Combine(runnerDir, "appsettings.json"), optional: true, reloadOnChange: false);

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

// ── HttpClient + Token Provider + API Agent ──
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ITokenProvider>(sp =>
{
    var cfg = sp.GetRequiredService<TestEnvironmentConfig>();
    if (!string.IsNullOrWhiteSpace(cfg.AuthUsername)
        && !string.IsNullOrWhiteSpace(cfg.AuthPassword)
        && string.IsNullOrWhiteSpace(cfg.AuthToken))
    {
        return new LoginTokenProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
            cfg,
            sp.GetRequiredService<ILogger<LoginTokenProvider>>());
    }
    return new StaticTokenProvider(cfg.AuthToken);
});
builder.Services.AddSingleton<ApiTestAgent>(sp => new ApiTestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<ApiTestAgent>>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<TestEnvironmentConfig>(),
    sp.GetRequiredService<ITokenProvider>()
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

// ── Persistence — share the same data directory as the Runner ──
var runnerBinDir = Path.GetFullPath(Path.Combine(runnerDir, "bin", "Debug", "net8.0"));
var dataDir = Directory.Exists(Path.Combine(runnerBinDir, "testsets"))
              || Directory.Exists(Path.Combine(runnerBinDir, "modules"))
    ? runnerBinDir
    : AppContext.BaseDirectory;

// Resolve the storage state path relative to the shared data directory so that
// auth state saved by the Runner CLI is found by the WebApi (and vice versa).
if (!string.IsNullOrEmpty(envConfig.BraveCloudUiStorageStatePath)
    && !Path.IsPathRooted(envConfig.BraveCloudUiStorageStatePath))
{
    envConfig.BraveCloudUiStorageStatePath = Path.Combine(dataDir, envConfig.BraveCloudUiStorageStatePath);
}

builder.Services.AddSingleton(new TestSetRepository(dataDir));
builder.Services.AddSingleton(new ExecutionHistoryRepository(dataDir));
builder.Services.AddSingleton(new ModuleRepository(dataDir));

// ── Orchestrator ──
builder.Services.AddSingleton<TestOrchestrator>();

// ── Run tracker (in-memory active runs) ──
builder.Services.AddSingleton<RunTracker>();
builder.Services.AddSingleton<ModuleRunTracker>();

// ── CORS (allow Vite dev server) ──
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod();
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

// ── Health check ──
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.Urls.Add("http://localhost:5050");
app.Run();
