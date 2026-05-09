using System.Text.Json;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.Persistence;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.EventAssertAgent;

/// <summary>
/// REQ-004 acceptance criterion #1 — EventAssertStepDefinition serialises
/// through the existing post-step JSON path unchanged.
///
/// Locks the persisted shape so a future refactor that, for example, renames
/// a property or drops camelCase serialisation surfaces immediately rather
/// than corrupting persisted test sets on save.
/// </summary>
public class EventAssertSerializationTests
{
    private static readonly JsonSerializerOptions PersistenceOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    [Fact]
    public void Round_trips_through_VerificationStep_carrier()
    {
        // Build a fully-populated EventAssertStepDefinition under a
        // VerificationStep — the on-wire shape PostStepsPanel saves through
        // PUT /post-steps/{parentKind}/{idx}/{postIdx}.
        var original = new VerificationStep
        {
            Description = "MeterReadingCreated raised",
            Target = "Event_AzureServiceBus",
            WaitBeforeSeconds = 5,
            Role = "Verification",
            EventAssert = new EventAssertStepDefinition
            {
                Name = "evt",
                ConnectionKey = "DefaultBus",
                Entity = new ServiceBusEntity
                {
                    Type = ServiceBusEntityType.Topic,
                    Name = "meter-events",
                    SubscriptionName = "test-runner",
                },
                BodyFormat = BodyFormat.Json,
                ReceiveMode = ReceiveMode.PeekLock,
                MatchMode = MatchMode.AnyMessage,
                ExpectedCount = null,
                MaxCount = null,
                TimeoutSeconds = 30,
                MaxMessages = 50,
                DrainBeforeParent = false,
                CompleteOnPass = true,
                CorrelationFilter = "{{TestRunId}}",
                SessionId = null,
                Criteria =
                {
                    new EventCriterion
                    {
                        Field = "ApplicationProperties.EventType",
                        Operator = AssertionOperator.Equals,
                        Expected = "MeterReadingCreated",
                        IgnoreCase = true,
                    },
                    new EventCriterion
                    {
                        Field = "Body.MeterId",
                        Operator = AssertionOperator.Equals,
                        Expected = "{{MeterId}}",
                    },
                },
                Captures =
                {
                    new EventCapture { Field = "MessageId", As = "MessageId", Required = true },
                    new EventCapture { Field = "Body.EventId", As = "EventId", Required = false },
                },
            },
        };

        var json = JsonSerializer.Serialize(original, PersistenceOpts);
        var rt = JsonSerializer.Deserialize<VerificationStep>(json, PersistenceOpts)!;

        rt.Target.Should().Be("Event_AzureServiceBus");
        rt.EventAssert.Should().NotBeNull();
        rt.EventAssert!.ConnectionKey.Should().Be("DefaultBus");
        rt.EventAssert.Entity.Type.Should().Be(ServiceBusEntityType.Topic);
        rt.EventAssert.Entity.Name.Should().Be("meter-events");
        rt.EventAssert.Entity.SubscriptionName.Should().Be("test-runner");
        rt.EventAssert.BodyFormat.Should().Be(BodyFormat.Json);
        rt.EventAssert.ReceiveMode.Should().Be(ReceiveMode.PeekLock);
        rt.EventAssert.MatchMode.Should().Be(MatchMode.AnyMessage);
        rt.EventAssert.CorrelationFilter.Should().Be("{{TestRunId}}");
        rt.EventAssert.Criteria.Should().HaveCount(2);
        rt.EventAssert.Criteria[0].Field.Should().Be("ApplicationProperties.EventType");
        rt.EventAssert.Criteria[1].Field.Should().Be("Body.MeterId");
        rt.EventAssert.Criteria[1].Expected.Should().Be("{{MeterId}}");
        rt.EventAssert.Captures.Should().HaveCount(2);
        rt.EventAssert.Captures[0].As.Should().Be("MessageId");
        rt.EventAssert.Captures[1].Required.Should().BeFalse();
    }

    [Fact]
    public void Enums_serialise_as_string_names_not_integers()
    {
        // The persistence layer uses JsonStringEnumConverter (attribute on each
        // enum). A regression that drops the attribute would silently switch
        // to integer enum values and break the chat assistant's prompt-emitted
        // JSON. Pin the wire shape explicitly.
        var step = new EventAssertStepDefinition
        {
            ConnectionKey = "k",
            Entity = new ServiceBusEntity { Type = ServiceBusEntityType.Topic, Name = "t" },
            BodyFormat = BodyFormat.Xml,
            ReceiveMode = ReceiveMode.ReceiveAndDelete,
            MatchMode = MatchMode.MaxCount,
        };

        var json = JsonSerializer.Serialize(step, PersistenceOpts);

        json.Should().Contain("\"type\":\"Topic\"");
        json.Should().Contain("\"bodyFormat\":\"Xml\"");
        json.Should().Contain("\"receiveMode\":\"ReceiveAndDelete\"");
        json.Should().Contain("\"matchMode\":\"MaxCount\"");
    }

    [Fact]
    public void Round_trips_through_PersistedTestSet()
    {
        // The "real" surface — a TestObjective owning an ApiTestDefinition
        // whose PostSteps include the event-assert. This is what the file
        // store / SQLite repo persists.
        var ts = new PersistedTestSet
        {
            Id = "ts",
            Name = "Test Set",
            Objective = "do the thing",
            TestObjectives =
            {
                new TestObjective
                {
                    Id = "obj-1",
                    Name = "create order",
                    TargetType = "API_REST",
                    ApiSteps =
                    {
                        new AiTestCrew.Agents.ApiAgent.ApiTestDefinition
                        {
                            Method = "POST",
                            Endpoint = "/orders",
                            PostSteps =
                            {
                                new VerificationStep
                                {
                                    Description = "OrderCreated event",
                                    Target = "Event_AzureServiceBus",
                                    WaitBeforeSeconds = 0,
                                    EventAssert = new EventAssertStepDefinition
                                    {
                                        ConnectionKey = "DefaultBus",
                                        Entity = new ServiceBusEntity
                                        {
                                            Type = ServiceBusEntityType.Queue,
                                            Name = "order-events",
                                        },
                                        MatchMode = MatchMode.ExactlyOne,
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
                                            new EventCapture { Field = "MessageId", As = "MsgId" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(ts, PersistenceOpts);
        var rt = JsonSerializer.Deserialize<PersistedTestSet>(json, PersistenceOpts)!;

        var post = rt.TestObjectives[0].ApiSteps[0].PostSteps[0];
        post.Target.Should().Be("Event_AzureServiceBus");
        post.EventAssert.Should().NotBeNull();
        post.EventAssert!.ConnectionKey.Should().Be("DefaultBus");
        post.EventAssert.MatchMode.Should().Be(MatchMode.ExactlyOne);
        post.EventAssert.Criteria.Single().Expected.Should().Be("OrderCreated");
        post.EventAssert.Captures.Single().As.Should().Be("MsgId");
    }

    [Fact]
    public void Older_test_sets_without_eventAssert_field_deserialise_unchanged()
    {
        // Forward-compat: a persisted post-step authored before REQ-004 (no
        // eventAssert key on the wire) must still load — the field should
        // come back null, not throw on deserialise.
        const string priorJson = """
        {
          "description": "old DB check",
          "target": "Db_SqlServer",
          "waitBeforeSeconds": 5,
          "role": "Verification",
          "dbCheck": { "name": "x", "connectionKey": "BravoDb", "sql": "SELECT 1", "timeoutSeconds": 15, "columnAssertions": [], "captures": [] }
        }
        """;

        var rt = JsonSerializer.Deserialize<VerificationStep>(priorJson, PersistenceOpts)!;
        rt.Target.Should().Be("Db_SqlServer");
        rt.DbCheck.Should().NotBeNull();
        rt.EventAssert.Should().BeNull();
    }
}
