using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.EventAssertAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.Environment;

public class StepParameterSubstituterTests
{
    [Fact]
    public void DbCheck_sql_and_column_assertions_substitute()
    {
        var def = new DbCheckStepDefinition
        {
            Name = "X",
            Sql = "SELECT * FROM Jobs WHERE MessageID = '{{MessageID}}'",
            ColumnAssertions =
            {
                new ColumnAssertion
                {
                    Column = "{{ColName}}",
                    JsonPath = "$.Items[{{Index}}].Code",
                    Expected = "{{Expected}}",
                    Expected2 = "{{Expected2}}",
                },
            },
        };

        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MessageID"] = "MSG-1",
            ["ColName"] = "Payload",
            ["Index"] = "0",
            ["Expected"] = "ABC",
            ["Expected2"] = "DEF",
        };

        var result = StepParameterSubstituter.Apply(def, ctx);

        result.Sql.Should().Be("SELECT * FROM Jobs WHERE MessageID = 'MSG-1'");
        result.ColumnAssertions[0].Column.Should().Be("Payload");
        result.ColumnAssertions[0].JsonPath.Should().Be("$.Items[0].Code");
        result.ColumnAssertions[0].Expected.Should().Be("ABC");
        result.ColumnAssertions[0].Expected2.Should().Be("DEF");
    }

    [Fact]
    public void Captures_As_is_NOT_substituted_but_Column_and_JsonPath_are()
    {
        var def = new DbCheckStepDefinition
        {
            Captures =
            {
                new ColumnCapture
                {
                    Column = "{{ColName}}",
                    JsonPath = "$.{{PathField}}",
                    As = "{{TokenName}}",  // must NOT be substituted
                    Required = true,
                },
            },
        };

        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ColName"] = "Payload",
            ["PathField"] = "OrderId",
            ["TokenName"] = "JobId",  // would be a footgun if it subbed in
        };

        var result = StepParameterSubstituter.Apply(def, ctx);

        result.Captures[0].Column.Should().Be("Payload");
        result.Captures[0].JsonPath.Should().Be("$.OrderId");
        result.Captures[0].As.Should().Be("{{TokenName}}",
            "Captures.As is the binding target — substituting it would let parent context redirect captures unexpectedly");
    }

    [Fact]
    public void DbCheck_returns_clone_not_mutated_source()
    {
        var def = new DbCheckStepDefinition
        {
            Sql = "SELECT '{{X}}'",
            ColumnAssertions = { new ColumnAssertion { Column = "C", Expected = "{{Y}}" } },
        };

        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X"] = "x",
            ["Y"] = "y",
        };

        var result = StepParameterSubstituter.Apply(def, ctx);

        result.Should().NotBeSameAs(def);
        def.Sql.Should().Be("SELECT '{{X}}'");  // original untouched
        def.ColumnAssertions[0].Expected.Should().Be("{{Y}}");
        result.Sql.Should().Be("SELECT 'x'");
        result.ColumnAssertions[0].Expected.Should().Be("y");
    }

    // ── REQ-004 EventAssertStepDefinition ──────────────────────────────

    [Fact]
    public void EventAssert_substitutes_entity_correlation_session_and_criteria()
    {
        var def = new EventAssertStepDefinition
        {
            Name = "evt",
            ConnectionKey = "DefaultBus",
            Entity = new ServiceBusEntity
            {
                Type = ServiceBusEntityType.Topic,
                Name = "{{Topic}}-events",
                SubscriptionName = "{{Sub}}",
            },
            CorrelationFilter = "{{CorrId}}",
            SessionId = "{{Session}}",
            Criteria =
            {
                new EventCriterion
                {
                    Field = "{{FieldPrefix}}.EventType",
                    Expected = "{{ExpectedEvent}}",
                    Expected2 = "{{Expected2}}",
                },
            },
        };

        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Topic"] = "meter",
            ["Sub"] = "test-runner",
            ["CorrId"] = "abc-123",
            ["Session"] = "sess-1",
            ["FieldPrefix"] = "ApplicationProperties",
            ["ExpectedEvent"] = "MeterReadingCreated",
            ["Expected2"] = "Y",
        };

        var result = StepParameterSubstituter.Apply(def, ctx);

        result.Entity.Name.Should().Be("meter-events");
        result.Entity.SubscriptionName.Should().Be("test-runner");
        result.CorrelationFilter.Should().Be("abc-123");
        result.SessionId.Should().Be("sess-1");
        result.Criteria[0].Field.Should().Be("ApplicationProperties.EventType");
        result.Criteria[0].Expected.Should().Be("MeterReadingCreated");
        result.Criteria[0].Expected2.Should().Be("Y");
    }

    [Fact]
    public void EventAssert_capture_As_is_NOT_substituted_but_Field_is()
    {
        var def = new EventAssertStepDefinition
        {
            ConnectionKey = "DefaultBus",
            Captures =
            {
                new EventCapture
                {
                    Field = "Body.{{PathField}}",
                    As = "{{TokenName}}",  // must NOT be substituted
                    Required = true,
                },
            },
        };

        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PathField"] = "OrderId",
            ["TokenName"] = "OrderId",
        };

        var result = StepParameterSubstituter.Apply(def, ctx);
        result.Captures[0].Field.Should().Be("Body.OrderId");
        result.Captures[0].As.Should().Be("{{TokenName}}",
            "EventCapture.As mirrors REQ-002's ColumnCapture.As — substituting would let parent context redirect captures");
    }

    [Fact]
    public void EventAssert_returns_clone_not_mutated_source()
    {
        var def = new EventAssertStepDefinition
        {
            ConnectionKey = "DefaultBus",
            Entity = new ServiceBusEntity { Name = "{{X}}" },
            Criteria = { new EventCriterion { Field = "F", Expected = "{{Y}}" } },
        };

        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X"] = "x",
            ["Y"] = "y",
        };

        var result = StepParameterSubstituter.Apply(def, ctx);

        result.Should().NotBeSameAs(def);
        def.Entity.Name.Should().Be("{{X}}");
        def.Criteria[0].Expected.Should().Be("{{Y}}");
        result.Entity.Name.Should().Be("x");
        result.Criteria[0].Expected.Should().Be("y");
    }

    [Fact]
    public void EventAssert_carries_through_unchanged_fields()
    {
        // Numeric / enum / bool fields aren't subject to substitution, but the
        // Apply overload must clone them through — easy to break with a paste-
        // and-edit refactor.
        var def = new EventAssertStepDefinition
        {
            ConnectionKey = "DefaultBus",
            BodyFormat = BodyFormat.Xml,
            ReceiveMode = ReceiveMode.ReceiveAndDelete,
            MatchMode = MatchMode.MaxCount,
            ExpectedCount = 0,
            MaxCount = 5,
            TimeoutSeconds = 90,
            MaxMessages = 100,
            DrainBeforeParent = true,
            CompleteOnPass = false,
            Entity = new ServiceBusEntity { Name = "q" },
        };

        var result = StepParameterSubstituter.Apply(def, new Dictionary<string, string>());

        result.BodyFormat.Should().Be(BodyFormat.Xml);
        result.ReceiveMode.Should().Be(ReceiveMode.ReceiveAndDelete);
        result.MatchMode.Should().Be(MatchMode.MaxCount);
        result.ExpectedCount.Should().Be(0);
        result.MaxCount.Should().Be(5);
        result.TimeoutSeconds.Should().Be(90);
        result.MaxMessages.Should().Be(100);
        result.DrainBeforeParent.Should().BeTrue();
        result.CompleteOnPass.Should().BeFalse();
    }
}
