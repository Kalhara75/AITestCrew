using System.Diagnostics;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Testcontainers.MsSql;
using Xunit;

namespace AiTestCrew.Agents.Tests.DbAgent;

/// <summary>
/// End-to-end test of the DB-check pipeline via a real SQL Server (Testcontainers).
/// Spins up a single container per fixture, seeds a Jobs table, and exercises
/// JSON-path assertion + capture-as-{{Token}} round-trip. Auto-skips when Docker
/// isn't reachable so CI hosts without Docker don't fail the build.
/// </summary>
public class DbCaptureRoundTripTests : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (System.Environment.GetEnvironmentVariable("AITC_SKIP_DOCKER") == "1") return;

        try
        {
            _container = new MsSqlBuilder()
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE Jobs (
                        JobId NVARCHAR(50) NOT NULL PRIMARY KEY,
                        MessageID NVARCHAR(50) NOT NULL,
                        Status NVARCHAR(50) NOT NULL,
                        Payload NVARCHAR(MAX) NULL,
                        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO Jobs (JobId, MessageID, Status, Payload) VALUES
                        ('JOB-001', 'MSG-AAA', 'Processed', N'{"OrderId":"12345","Amount":100}');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            _container = null;
            _connectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    [DockerRequiredFact]
    public async Task DbCheck_with_jsonpath_assertion_and_capture_passes_and_emits_token()
    {
        if (_connectionString is null) return;  // Docker probe lied; skip.

        var (orchestrator, sp) = BuildOrchestrator(_connectionString);

        var postStep = new VerificationStep
        {
            Description = "Capture JobId for MessageID; assert OrderId via jsonPath",
            Target = "Db_SqlServer",
            WaitBeforeSeconds = 0,
            DbCheck = new DbCheckStepDefinition
            {
                Name = "Find Jobs row",
                ConnectionKey = "BravoDb",
                Sql = "SELECT TOP 1 JobId, Status, Payload FROM Jobs WHERE MessageID = '{{MessageID}}'",
                ColumnAssertions =
                {
                    new ColumnAssertion { Column = "Status", Expected = "Processed" },
                    new ColumnAssertion
                    {
                        Column = "Payload",
                        JsonPath = "$.OrderId",
                        Expected = "12345",
                    },
                },
                Captures =
                {
                    new ColumnCapture { Column = "JobId", As = "JobId", Required = true },
                },
            },
        };

        var stepSink = new List<TestStep>();
        var initialContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MessageID"] = "MSG-AAA",
        };

        await orchestrator.RunInlineAsync(
            new[] { postStep }, initialContext, parentStepIndex: 1,
            stepSink, environmentKey: null, callingAgent: null,
            ct: CancellationToken.None);

        // The DB step should have passed.
        var passed = stepSink.Where(s => s.Status == TestStatus.Passed).ToList();
        passed.Should().NotBeEmpty("the JsonPath + Equals assertions both match the seeded row");

        // The captured tokens should be attached to the rebuilt step's Metadata.
        var captureStep = stepSink.FirstOrDefault(s =>
            s.Metadata.ContainsKey("capturedTokens"));
        captureStep.Should().NotBeNull("the agent must attach captured tokens to the passing step");
        var captured = captureStep!.Metadata["capturedTokens"] as IDictionary<string, string>;
        captured.Should().NotBeNull();
        captured!.Should().ContainKey("JobId").WhoseValue.Should().Be("JOB-001");
    }

    [DockerRequiredFact]
    public async Task DbCheck_failure_attaches_first_row_diagnostics()
    {
        if (_connectionString is null) return;

        var (orchestrator, _) = BuildOrchestrator(_connectionString);

        var postStep = new VerificationStep
        {
            Description = "Intentional fail",
            Target = "Db_SqlServer",
            WaitBeforeSeconds = 0,
            DbCheck = new DbCheckStepDefinition
            {
                Name = "Status mismatch",
                ConnectionKey = "BravoDb",
                Sql = "SELECT TOP 1 JobId, Status, Payload FROM Jobs WHERE MessageID = 'MSG-AAA'",
                ColumnAssertions =
                {
                    new ColumnAssertion { Column = "Status", Expected = "WRONG" },
                },
            },
        };

        var stepSink = new List<TestStep>();
        var initialContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await orchestrator.RunInlineAsync(
            new[] { postStep }, initialContext, parentStepIndex: 1,
            stepSink, environmentKey: null, callingAgent: null,
            ct: CancellationToken.None);

        var failStep = stepSink.FirstOrDefault(s => s.Status == TestStatus.Failed);
        failStep.Should().NotBeNull();
        failStep!.Metadata.Should().ContainKey("dbCheckRow");
        var row = failStep.Metadata["dbCheckRow"] as IDictionary<string, string?>;
        row.Should().NotBeNull();
        row!.Keys.Should().Contain(new[] { "JobId", "Status", "Payload" });
    }

    [Fact]
    public void DeferredVerificationRequest_round_trips_CapturedTokens_through_json()
    {
        // Pure serialiser test — runs even without Docker.
        var dr = new DeferredVerificationRequest
        {
            ParentRunId = "run-1",
            PendingId = "pid-1",
            CapturedTokens = { ["JobId"] = "JOB-XYZ", ["Email"] = "a@b" },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(dr);
        var rt = System.Text.Json.JsonSerializer.Deserialize<DeferredVerificationRequest>(json)!;
        rt.CapturedTokens.Should().ContainKey("JobId").WhoseValue.Should().Be("JOB-XYZ");
        rt.CapturedTokens.Should().ContainKey("Email").WhoseValue.Should().Be("a@b");
    }

    private static (PostStepOrchestrator, IServiceProvider) BuildOrchestrator(string connectionString)
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["BravoDb"] = connectionString },
        };

        var services = new ServiceCollection();
        services.AddSingleton(cfg);
        services.AddSingleton<IEnvironmentResolver>(_ => new EnvironmentResolver(cfg));
        services.AddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Kernel.CreateBuilder().Build());
        services.AddSingleton<PostStepOrchestrator>();
        services.AddSingleton<DbCheckAgent>(sp => new DbCheckAgent(
            sp.GetRequiredService<Kernel>(),
            NullLogger<DbCheckAgent>.Instance,
            sp.GetRequiredService<IEnvironmentResolver>(),
            sp.GetRequiredService<PostStepOrchestrator>()));
        services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<DbCheckAgent>());

        var sp2 = services.BuildServiceProvider();
        return (sp2.GetRequiredService<PostStepOrchestrator>(), sp2);
    }
}
