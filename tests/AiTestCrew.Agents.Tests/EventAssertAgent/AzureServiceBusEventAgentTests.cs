using System.Text;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace AiTestCrew.Agents.Tests.EventAssertAgent;

public class AzureServiceBusEventAgentTests
{
    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    private const string ConnectionKey = "DefaultBus";
    private const string EnvKey = "test-env";

    private static (AzureServiceBusEventAgent agent, FakeServiceBusReceiverFactory fake)
        Build()
    {
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                [ConnectionKey] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKey=k",
                },
            },
            Environments = { [EnvKey] = new EnvironmentConfig() },
        };
        var fake = new FakeServiceBusReceiverFactory();

        var services = new ServiceCollection();
        services.AddSingleton(cfg);
        services.AddSingleton<IEnvironmentResolver>(_ => new EnvironmentResolver(cfg));
        services.AddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Kernel.CreateBuilder().Build());
        services.AddSingleton<IServiceBusReceiverFactory>(fake);
        services.AddSingleton<PostStepOrchestrator>();
        services.AddSingleton<AzureServiceBusEventAgent>(sp => new AzureServiceBusEventAgent(
            sp.GetRequiredService<Kernel>(),
            sp.GetRequiredService<ILogger<AzureServiceBusEventAgent>>(),
            sp.GetRequiredService<IEnvironmentResolver>(),
            sp.GetRequiredService<IServiceBusReceiverFactory>(),
            sp.GetRequiredService<PostStepOrchestrator>()));

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<AzureServiceBusEventAgent>(), fake);
    }

    private static TestTask BuildTask(List<EventAssertStepDefinition> defs) => new()
    {
        Description = "test",
        Target = TestTargetType.Event_AzureServiceBus,
        Parameters =
        {
            ["EnvironmentKey"] = EnvKey,
            ["PreloadedTestCases"] = defs,
        },
    };

    private static EventAssertStepDefinition Def(
        MatchMode matchMode = MatchMode.AnyMessage,
        int? expectedCount = null,
        int? maxCount = null,
        ReceiveMode receiveMode = ReceiveMode.PeekLock,
        bool completeOnPass = true,
        params EventCriterion[] criteria) => new()
    {
        Name = "test event",
        ConnectionKey = ConnectionKey,
        Entity = new ServiceBusEntity { Type = ServiceBusEntityType.Queue, Name = "q" },
        MatchMode = matchMode,
        ExpectedCount = expectedCount,
        MaxCount = maxCount,
        ReceiveMode = receiveMode,
        CompleteOnPass = completeOnPass,
        TimeoutSeconds = 1,
        MaxMessages = 50,
        Criteria = criteria.ToList(),
    };

    // ── AnyMessage / AllMessages / ExactlyOne ──────────────────────────

    [Fact]
    public async Task AnyMessage_passes_when_one_message_matches()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "1",
            ContentType = "application/json",
            Body = U("{\"Status\":\"Created\"}"),
        });
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "2",
            ContentType = "application/json",
            Body = U("{\"Status\":\"Other\"}"),
        });

        var def = Def(matchMode: MatchMode.AnyMessage,
            criteria: new EventCriterion
            {
                Field = "Body.Status",
                Operator = AssertionOperator.Equals,
                Expected = "Created",
            });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Passed);
    }

    [Fact]
    public async Task AllMessages_fails_on_empty_queue()
    {
        var (agent, _) = Build();
        var def = Def(matchMode: MatchMode.AllMessages,
            criteria: new EventCriterion { Field = "MessageId", Expected = "x" });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Failed);
        result.Steps[0].Summary.Should().Contain("no messages received");
    }

    [Fact]
    public async Task AllMessages_passes_when_every_message_matches()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "1",
            ApplicationProperties = new Dictionary<string, object?> { ["EventType"] = "X" },
        });
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "2",
            ApplicationProperties = new Dictionary<string, object?> { ["EventType"] = "X" },
        });

        var def = Def(matchMode: MatchMode.AllMessages,
            criteria: new EventCriterion
            {
                Field = "ApplicationProperties.EventType",
                Expected = "X",
            });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Passed);
    }

    [Fact]
    public async Task ExactlyOne_fails_when_two_match()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView { MessageId = "1" });
        fake.Enqueue(new ReceivedMessageView { MessageId = "1" });

        var def = Def(matchMode: MatchMode.ExactlyOne,
            criteria: new EventCriterion { Field = "MessageId", Expected = "1" });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Failed);
    }

    // ── MaxCount(0) — negative assertion ───────────────────────────────

    [Fact]
    public async Task MaxCount_zero_passes_when_no_match_arrives()
    {
        var (agent, fake) = Build();
        // Some traffic exists, but none of it matches the criterion — that's pass.
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "1",
            ApplicationProperties = new Dictionary<string, object?> { ["EventType"] = "Other" },
        });

        var def = Def(matchMode: MatchMode.MaxCount, expectedCount: 0,
            criteria: new EventCriterion
            {
                Field = "ApplicationProperties.EventType",
                Expected = "Rejected",
            });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Passed);
        result.Steps[0].Summary.Should().Contain("negative assertion");
    }

    [Fact]
    public async Task MaxCount_zero_fails_when_one_match_arrives()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "1",
            ApplicationProperties = new Dictionary<string, object?> { ["EventType"] = "Rejected" },
        });

        var def = Def(matchMode: MatchMode.MaxCount, expectedCount: 0,
            criteria: new EventCriterion
            {
                Field = "ApplicationProperties.EventType",
                Expected = "Rejected",
            });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Failed);
    }

    // ── Captures ───────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_emits_first_passing_message_value_into_metadata()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "msg-A",
            ContentType = "application/json",
            Body = U("{\"OrderId\":\"42\"}"),
        });

        var def = Def(matchMode: MatchMode.AnyMessage,
            criteria: new EventCriterion { Field = "Body.OrderId", Expected = "42" });
        def.Captures.Add(new EventCapture { Field = "MessageId", As = "MessageId", Required = true });
        def.Captures.Add(new EventCapture { Field = "Body.OrderId", As = "OrderId", Required = true });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Passed);

        var passStep = result.Steps.Single();
        passStep.Metadata.Should().ContainKey("capturedTokens");
        var captured = (IDictionary<string, string>)passStep.Metadata["capturedTokens"]!;
        captured["MessageId"].Should().Be("msg-A");
        captured["OrderId"].Should().Be("42");
    }

    [Fact]
    public async Task Required_capture_failure_fails_the_step()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "msg",
            ContentType = "application/json",
            Body = U("{\"OrderId\":\"42\"}"),
        });

        var def = Def(matchMode: MatchMode.AnyMessage,
            criteria: new EventCriterion { Field = "Body.OrderId", Expected = "42" });
        def.Captures.Add(new EventCapture { Field = "Body.NonExistent", As = "X", Required = true });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Failed);
        result.Steps[0].Summary.Should().Contain("Capture failed");
    }

    // ── Settlement (PeekLock) ──────────────────────────────────────────

    [Fact]
    public async Task PeekLock_complete_on_pass_completes_passing_messages_and_abandons_others()
    {
        var (agent, fake) = Build();
        var passing = fake.Enqueue(new ReceivedMessageView { MessageId = "pass" });
        var failing = fake.Enqueue(new ReceivedMessageView { MessageId = "fail" });

        var def = Def(matchMode: MatchMode.AnyMessage,
            receiveMode: ReceiveMode.PeekLock,
            completeOnPass: true,
            criteria: new EventCriterion { Field = "MessageId", Expected = "pass" });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Passed);

        fake.CompletedMessageIds.Should().ContainSingle().Which.Should().Be("pass");
        fake.AbandonedMessageIds.Should().ContainSingle().Which.Should().Be("fail");
    }

    [Fact]
    public async Task PeekLock_fail_abandons_all()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView { MessageId = "1" });
        fake.Enqueue(new ReceivedMessageView { MessageId = "2" });

        var def = Def(matchMode: MatchMode.AnyMessage,
            receiveMode: ReceiveMode.PeekLock,
            criteria: new EventCriterion { Field = "MessageId", Expected = "no-match" });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Failed);

        fake.CompletedMessageIds.Should().BeEmpty();
        fake.AbandonedMessageIds.Should().BeEquivalentTo(new[] { "1", "2" });
    }

    [Fact]
    public async Task PeekLock_pass_with_completeOnPass_false_abandons_all()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView { MessageId = "x" });

        var def = Def(matchMode: MatchMode.AnyMessage,
            receiveMode: ReceiveMode.PeekLock,
            completeOnPass: false,
            criteria: new EventCriterion { Field = "MessageId", Expected = "x" });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Passed);
        fake.CompletedMessageIds.Should().BeEmpty();
        fake.AbandonedMessageIds.Should().ContainSingle().Which.Should().Be("x");
    }

    // ── CorrelationFilter ──────────────────────────────────────────────

    [Fact]
    public async Task Correlation_filter_skips_non_matching_messages()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView { MessageId = "1", CorrelationId = "other" });
        fake.Enqueue(new ReceivedMessageView { MessageId = "2", CorrelationId = "mine" });

        var def = Def(matchMode: MatchMode.ExactlyOne,
            criteria: new EventCriterion { Field = "MessageId", Expected = "2" });
        def.CorrelationFilter = "mine";

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Passed);
    }

    // ── Diagnostics ────────────────────────────────────────────────────

    [Fact]
    public async Task Failed_step_includes_serviceBusReceived_diagnostics()
    {
        var (agent, fake) = Build();
        fake.Enqueue(new ReceivedMessageView { MessageId = "1" });

        var def = Def(matchMode: MatchMode.AnyMessage,
            criteria: new EventCriterion { Field = "MessageId", Expected = "no-match" });

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Failed);
        result.Steps[0].Metadata.Should().ContainKey("serviceBusReceived");
    }

    // ── Config / wiring errors ─────────────────────────────────────────

    [Fact]
    public async Task Unknown_connection_key_yields_Error_status()
    {
        var (agent, _) = Build();
        var def = Def();
        def.ConnectionKey = "DoesNotExist";

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Error);
        result.Steps[0].Summary.Should().Contain("not configured");
    }

    [Fact]
    public async Task Topic_without_subscription_yields_Error_status()
    {
        var (agent, _) = Build();
        var def = Def();
        def.Entity = new ServiceBusEntity { Type = ServiceBusEntityType.Topic, Name = "t" };

        var result = await agent.ExecuteAsync(BuildTask(new() { def }), default);
        result.Status.Should().Be(TestStatus.Error);
        result.Steps[0].Summary.Should().Contain("SubscriptionName");
    }

    [Fact]
    public async Task Missing_PreloadedTestCases_yields_Error_status()
    {
        var (agent, _) = Build();
        var task = new TestTask
        {
            Description = "test",
            Target = TestTargetType.Event_AzureServiceBus,
        };
        var result = await agent.ExecuteAsync(task, default);
        result.Status.Should().Be(TestStatus.Error);
    }

    [Fact]
    public async Task CanHandleAsync_only_accepts_Event_AzureServiceBus_target()
    {
        var (agent, _) = Build();
        (await agent.CanHandleAsync(new TestTask
        {
            Description = "x",
            Target = TestTargetType.Event_AzureServiceBus,
        })).Should().BeTrue();
        (await agent.CanHandleAsync(new TestTask
        {
            Description = "x",
            Target = TestTargetType.Db_SqlServer,
        })).Should().BeFalse();
    }
}
