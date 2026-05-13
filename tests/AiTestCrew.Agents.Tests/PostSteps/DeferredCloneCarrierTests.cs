using System.Reflection;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Agents.Shared;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.PostSteps;

/// <summary>
/// Regression for the dropped <see cref="VerificationStep.EventAssert"/> carrier
/// on the deferred replay path. When a post-step with a non-trivial
/// <c>WaitBeforeSeconds</c> goes through <c>PostStepOrchestrator.RunDeferredAttemptAsync</c>
/// instead of <c>RunInlineAsync</c>, the orchestrator rebuilds the
/// <see cref="VerificationStep"/> to adjust the wait. Originally that rebuild
/// hand-copied each carrier field and forgot <see cref="VerificationStep.EventAssert"/>
/// after REQ-004 was added — the deferred path then reported
/// "EventAssert payload is missing on the post-step" while the standalone
/// "Run" button (which uses the inline path) worked. The reflection-based
/// guard test below ensures any future carrier added to <see cref="VerificationStep"/>
/// is exercised here automatically.
/// </summary>
public class DeferredCloneCarrierTests
{
    [Fact]
    public void Clone_preserves_EventAssert_payload_on_deferred_replay()
    {
        var original = new VerificationStep
        {
            Description = "InvoiceCompleted event on partner outbound queue",
            Target = "Event_AzureServiceBus",
            WaitBeforeSeconds = 60,
            EventAssert = new EventAssertStepDefinition
            {
                Name = "InvoiceCompleted",
                ConnectionKey = "DefaultBus",
                Entity = new ServiceBusEntity { Name = "tesla_dev_partner_outbound_queue" },
                MatchMode = MatchMode.AnyMessage,
                TimeoutSeconds = 60,
                MaxMessages = 50,
            },
        };

        var clone = PostStepOrchestrator.CloneForReplay(original, isFirst: true, firstWait: 60);

        clone.EventAssert.Should().NotBeNull(
            "the deferred replay must not drop the EventAssert payload — otherwise " +
            "TryPreloadPayload reports 'EventAssert payload is missing on the post-step'.");
        clone.EventAssert!.Entity.Name.Should().Be("tesla_dev_partner_outbound_queue");
    }

    /// <summary>
    /// Reflection guard: for every nullable reference-typed payload property on
    /// <see cref="VerificationStep"/>, the deferred clone must preserve a non-null
    /// instance. New carriers added to <see cref="VerificationStep"/> are picked
    /// up automatically — this test fails if the next contributor forgets to
    /// extend <c>PostStepOrchestrator.CloneForReplay</c>.
    /// </summary>
    [Fact]
    public void Clone_preserves_every_carrier_property_on_VerificationStep()
    {
        var carrierTypes = new Dictionary<string, Type>
        {
            [nameof(VerificationStep.WebUi)] = typeof(WebUiTestDefinition),
            [nameof(VerificationStep.DesktopUi)] = typeof(DesktopUiTestDefinition),
            [nameof(VerificationStep.Api)] = typeof(ApiTestDefinition),
            [nameof(VerificationStep.AseXml)] = typeof(AseXmlTestDefinition),
            [nameof(VerificationStep.AseXmlDeliver)] = typeof(AseXmlDeliveryTestDefinition),
            [nameof(VerificationStep.DbCheck)] = typeof(DbCheckStepDefinition),
            [nameof(VerificationStep.EventAssert)] = typeof(EventAssertStepDefinition),
        };

        // Sanity: surface any new carrier on VerificationStep that the test doesn't
        // know about, so we don't quietly stop guarding it.
        var actualCarriers = typeof(VerificationStep)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string))
            .Select(p => p.Name)
            .ToHashSet();
        carrierTypes.Keys.ToHashSet().SetEquals(actualCarriers).Should().BeTrue(
            "VerificationStep gained or lost a carrier property — update " +
            $"{nameof(DeferredCloneCarrierTests)} and {nameof(PostStepOrchestrator)}.CloneForReplay. " +
            $"Expected: [{string.Join(", ", carrierTypes.Keys.OrderBy(s => s))}] " +
            $"Actual: [{string.Join(", ", actualCarriers.OrderBy(s => s))}]");

        foreach (var (propName, type) in carrierTypes)
        {
            var original = new VerificationStep { Target = "X", WaitBeforeSeconds = 30 };
            var payload = Activator.CreateInstance(type)!;
            typeof(VerificationStep).GetProperty(propName)!.SetValue(original, payload);

            var clone = PostStepOrchestrator.CloneForReplay(original, isFirst: true, firstWait: 30);

            var cloned = typeof(VerificationStep).GetProperty(propName)!.GetValue(clone);
            cloned.Should().BeSameAs(payload,
                $"{propName} must be carried through CloneForReplay — otherwise the " +
                $"deferred replay path will report '{propName} payload is missing on the post-step' " +
                $"for any post-step whose WaitBeforeSeconds exceeds the defer threshold.");
        }
    }
}
