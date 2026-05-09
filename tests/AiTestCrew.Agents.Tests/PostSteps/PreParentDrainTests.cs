using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Agents.Tests.EventAssertAgent;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace AiTestCrew.Agents.Tests.PostSteps;

public class PreParentDrainTests
{
    private const string ConnectionKey = "DefaultBus";

    private static (PostStepOrchestrator orchestrator, FakeServiceBusReceiverFactory fake)
        Build()
    {
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                [ConnectionKey] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "Endpoint=sb://x;SharedAccessKey=y",
                },
            },
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
        return (sp.GetRequiredService<PostStepOrchestrator>(), fake);
    }

    private static VerificationStep DrainPostStep(string entityName = "q", bool drain = true)
    {
        return new VerificationStep
        {
            Description = "drain",
            Target = "Event_AzureServiceBus",
            EventAssert = new EventAssertStepDefinition
            {
                Name = "drain",
                ConnectionKey = ConnectionKey,
                Entity = new ServiceBusEntity { Name = entityName },
                DrainBeforeParent = drain,
            },
        };
    }

    [Fact]
    public void HasDrainBeforeParent_detects_at_least_one()
    {
        PostStepOrchestrator.HasDrainBeforeParent(Array.Empty<VerificationStep>())
            .Should().BeFalse();
        PostStepOrchestrator.HasDrainBeforeParent(new[] { DrainPostStep(drain: false) })
            .Should().BeFalse();
        PostStepOrchestrator.HasDrainBeforeParent(new[]
        {
            DrainPostStep(drain: false),
            DrainPostStep(drain: true),
        }).Should().BeTrue();
    }

    [Fact]
    public async Task Drain_clears_pending_messages_before_parent_runs()
    {
        var (orchestrator, fake) = Build();
        // Pre-populate the fake with stale messages.
        fake.Enqueue(new ReceivedMessageView { MessageId = "stale-1" });
        fake.Enqueue(new ReceivedMessageView { MessageId = "stale-2" });
        fake.Enqueue(new ReceivedMessageView { MessageId = "stale-3" });

        await orchestrator.RunPreParentDrainsAsync(
            new[] { DrainPostStep() },
            new Dictionary<string, string>(),
            environmentKey: null,
            ct: default);

        fake.PendingMessages.Should().BeEmpty("the drain should consume every stale message");
    }

    [Fact]
    public async Task Drain_substitutes_tokens_in_entity_name()
    {
        var (orchestrator, fake) = Build();
        fake.Enqueue(new ReceivedMessageView { MessageId = "x" });

        await orchestrator.RunPreParentDrainsAsync(
            new[] { DrainPostStep(entityName: "{{Customer}}-events") },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Customer"] = "sumo" },
            environmentKey: null,
            ct: default);

        fake.Opened.Should().ContainSingle();
        fake.Opened[0].Entity.Should().Be("sumo-events");
    }

    [Fact]
    public async Task Drain_no_op_when_no_post_step_requests_drain()
    {
        var (orchestrator, fake) = Build();
        fake.Enqueue(new ReceivedMessageView { MessageId = "should-survive" });

        await orchestrator.RunPreParentDrainsAsync(
            new[] { DrainPostStep(drain: false) },
            new Dictionary<string, string>(),
            environmentKey: null,
            ct: default);

        // The non-drain post-step is ignored entirely — the queue still has the message.
        fake.PendingMessages.Should().HaveCount(1);
        fake.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Drain_with_blank_entity_name_throws()
    {
        var (orchestrator, _) = Build();

        Func<Task> act = () => orchestrator.RunPreParentDrainsAsync(
            new[] { DrainPostStep(entityName: "") },
            new Dictionary<string, string>(),
            environmentKey: null,
            ct: default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no entity name*");
    }

    [Fact]
    public async Task Drain_with_unknown_connection_key_throws()
    {
        var (orchestrator, _) = Build();
        var post = DrainPostStep();
        post.EventAssert!.ConnectionKey = "DoesNotExist";

        Func<Task> act = () => orchestrator.RunPreParentDrainsAsync(
            new[] { post }, new Dictionary<string, string>(),
            environmentKey: null, ct: default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task Drain_topic_without_subscription_throws()
    {
        var (orchestrator, _) = Build();
        var post = DrainPostStep();
        post.EventAssert!.Entity = new ServiceBusEntity
        {
            Type = ServiceBusEntityType.Topic,
            Name = "t",
            SubscriptionName = null,
        };

        Func<Task> act = () => orchestrator.RunPreParentDrainsAsync(
            new[] { post }, new Dictionary<string, string>(),
            environmentKey: null, ct: default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SubscriptionName*");
    }
}
