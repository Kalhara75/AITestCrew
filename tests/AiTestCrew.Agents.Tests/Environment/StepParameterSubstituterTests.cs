using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Environment;
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
}
