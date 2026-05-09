using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Agents.Tests.EventAssertAgent;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace AiTestCrew.Agents.Tests.PostSteps;

/// <summary>
/// REQ-004 acceptance criterion #6 (inline path) — proves the orchestrator's
/// capture-token merge works for event-assert post-steps the same way it does
/// for DB asserts. A first event-assert captures <c>MessageId</c>; a sibling
/// event-assert references <c>{{MessageId}}</c> in its criterion. If the
/// substitution round-trips cleanly, the second post-step's criterion matches
/// the message it was supposed to.
///
/// <para>
/// Uses the existing <c>FakeServiceBusReceiverFactory</c> so the test runs
/// without any external dependency. The deferred-path equivalent (criterion
/// #7) requires the run-queue / pending-verification repos and is exercised
/// transitively by the existing REQ-002 capture-token round-trip plumbing
/// (<c>DeferredVerificationRequest.CapturedTokens</c> is shape-agnostic — it
/// just round-trips a <c>Dictionary&lt;string,string&gt;</c>).
/// </para>
/// </summary>
public class EventCaptureRoundTripTests
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

        // Register the agent as itself AND as ITestAgent — the orchestrator's
        // ResolveSiblings reads from GetServices<ITestAgent>() and needs to
        // find the event-assert agent there.
        services.AddSingleton<AzureServiceBusEventAgent>(sp => new AzureServiceBusEventAgent(
            sp.GetRequiredService<Kernel>(),
            sp.GetRequiredService<ILogger<AzureServiceBusEventAgent>>(),
            sp.GetRequiredService<IEnvironmentResolver>(),
            sp.GetRequiredService<IServiceBusReceiverFactory>(),
            sp.GetRequiredService<PostStepOrchestrator>()));
        services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<AzureServiceBusEventAgent>());

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<PostStepOrchestrator>(), fake);
    }

    [Fact]
    public async Task Capture_from_first_post_step_substitutes_into_second_post_steps_criterion()
    {
        var (orchestrator, fake) = Build();

        // Two messages on the queue:
        //  - msg-A: drained by post-step 1; captures MessageId="abc-123".
        //  - msg-B: drained by post-step 2; matches IFF the {{MessageId}} token
        //           was substituted from post-step 1's capture.
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "abc-123",
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["EventType"] = "OrderCreated",
            },
        });
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "def-456",
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["EventType"] = "OrderShipped",
                ["ParentMessageId"] = "abc-123",
            },
        });

        var captureStep = new VerificationStep
        {
            Description = "Capture parent message id",
            Target = "Event_AzureServiceBus",
            WaitBeforeSeconds = 0,
            EventAssert = new EventAssertStepDefinition
            {
                Name = "capture order created",
                ConnectionKey = ConnectionKey,
                Entity = new ServiceBusEntity { Name = "order-events" },
                MatchMode = MatchMode.AnyMessage,
                MaxMessages = 1,                  // drain ONLY msg-A
                TimeoutSeconds = 1,
                Criteria =
                {
                    new EventCriterion
                    {
                        Field = "ApplicationProperties.EventType",
                        Operator = AssertionOperator.Equals,
                        Expected = "OrderCreated",
                    },
                },
                Captures =
                {
                    new EventCapture { Field = "MessageId", As = "MessageId", Required = true },
                },
            },
        };

        var consumerStep = new VerificationStep
        {
            Description = "Verify follow-up references the captured id",
            Target = "Event_AzureServiceBus",
            WaitBeforeSeconds = 0,
            EventAssert = new EventAssertStepDefinition
            {
                Name = "shipment references parent",
                ConnectionKey = ConnectionKey,
                Entity = new ServiceBusEntity { Name = "order-events" },
                MatchMode = MatchMode.AnyMessage,
                TimeoutSeconds = 1,
                Criteria =
                {
                    new EventCriterion
                    {
                        Field = "ApplicationProperties.ParentMessageId",
                        Operator = AssertionOperator.Equals,
                        Expected = "{{MessageId}}",   // ← bound from captureStep
                    },
                },
            },
        };

        var stepSink = new List<TestStep>();
        await orchestrator.RunInlineAsync(
            new[] { captureStep, consumerStep },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            parentStepIndex: 1,
            stepSink,
            environmentKey: null,
            callingAgent: null,
            ct: default);

        // Both post-steps must have produced at least one Pass step.
        var passes = stepSink.Where(s => s.Status == TestStatus.Passed).ToList();
        passes.Should().HaveCountGreaterThanOrEqualTo(2,
            "both event-assert post-steps should pass when the capture round-tripped cleanly");

        // The first must carry capturedTokens with MessageId.
        var captureCarrier = stepSink.FirstOrDefault(s =>
            s.Metadata.ContainsKey("capturedTokens"));
        captureCarrier.Should().NotBeNull("post-step 1 must attach captured tokens");
        var captured = captureCarrier!.Metadata["capturedTokens"] as IDictionary<string, string>;
        captured.Should().NotBeNull();
        captured!.Should().ContainKey("MessageId").WhoseValue.Should().Be("abc-123");
    }

    [Fact]
    public async Task Sibling_post_step_sees_unsubstituted_token_when_capture_is_optional_and_missing()
    {
        // Negative version of the round-trip: when the captured field doesn't
        // resolve and the capture is optional, the orchestrator leaves the
        // sibling's {{Token}} as a literal. The sibling's criterion will then
        // fail (since "abc-123" ≠ "{{MessageId}}"), proving the substitution
        // was actually attempted from the working context — not silently
        // bypassed.
        var (orchestrator, fake) = Build();

        // Single message with no MessageId — the optional capture leaves
        // {{MessageId}} undefined.
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = null,
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["EventType"] = "OrderCreated",
                ["ParentMessageId"] = "abc-123",
            },
        });

        var captureStep = new VerificationStep
        {
            Description = "Optional capture of missing field",
            Target = "Event_AzureServiceBus",
            WaitBeforeSeconds = 0,
            EventAssert = new EventAssertStepDefinition
            {
                Name = "capture",
                ConnectionKey = ConnectionKey,
                Entity = new ServiceBusEntity { Name = "order-events" },
                MatchMode = MatchMode.AnyMessage,
                MaxMessages = 1,
                TimeoutSeconds = 1,
                Criteria =
                {
                    new EventCriterion
                    {
                        Field = "ApplicationProperties.EventType",
                        Expected = "OrderCreated",
                    },
                },
                Captures =
                {
                    new EventCapture
                    {
                        Field = "MessageId",
                        As = "MessageId",
                        Required = false,        // ← key — won't fail the step
                    },
                },
            },
        };

        var consumerStep = new VerificationStep
        {
            Description = "Use the (undefined) captured id",
            Target = "Event_AzureServiceBus",
            WaitBeforeSeconds = 0,
            EventAssert = new EventAssertStepDefinition
            {
                Name = "consumer",
                ConnectionKey = ConnectionKey,
                Entity = new ServiceBusEntity { Name = "order-events" },
                MatchMode = MatchMode.AnyMessage,
                TimeoutSeconds = 1,
                Criteria =
                {
                    new EventCriterion
                    {
                        Field = "ApplicationProperties.ParentMessageId",
                        Expected = "{{MessageId}}",
                    },
                },
            },
        };

        // Pre-stage a second message that the consumer can drain — but its
        // ParentMessageId is "abc-123", which won't equal the literal
        // "{{MessageId}}" the consumer ends up comparing against.
        fake.Enqueue(new ReceivedMessageView
        {
            MessageId = "shipment",
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["EventType"] = "OrderShipped",
                ["ParentMessageId"] = "abc-123",
            },
        });

        var stepSink = new List<TestStep>();
        await orchestrator.RunInlineAsync(
            new[] { captureStep, consumerStep },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            parentStepIndex: 1,
            stepSink,
            environmentKey: null,
            callingAgent: null,
            ct: default);

        // Step 1 passed (criterion matched, optional capture missing → step
        // still passes, token left undefined).
        // Step 2 failed (literal {{MessageId}} ≠ "abc-123").
        stepSink.Any(s => s.Status == TestStatus.Failed)
            .Should().BeTrue("step 2's criterion must fail when {{MessageId}} couldn't be substituted");
    }
}
