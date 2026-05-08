using System.Text.Json;
using AiTestCrew.Agents.DbAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.DbAgent;

public class DbCheckStepDefinitionTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Legacy_expectedColumnValues_promotes_into_columnAssertions()
    {
        // Arrange — simulate a test set saved before REQ-002 with the old dict shape.
        var legacyJson = """
            {
              "name": "Legacy check",
              "connectionKey": "BravoDb",
              "sql": "SELECT * FROM Jobs WHERE MessageID = '{{MessageID}}'",
              "expectedColumnValues": { "Status": "Processed", "ApprovedBy": "system" },
              "timeoutSeconds": 15
            }
            """;

        // Act
        var def = JsonSerializer.Deserialize<DbCheckStepDefinition>(legacyJson, JsonOpts)!;

        // Assert
        def.ColumnAssertions.Should().HaveCount(2);
        def.ColumnAssertions.Should().Contain(a =>
            a.Column == "Status" && a.Operator == AssertionOperator.Equals && a.Expected == "Processed");
        def.ColumnAssertions.Should().Contain(a =>
            a.Column == "ApprovedBy" && a.Operator == AssertionOperator.Equals && a.Expected == "system");
        def.Captures.Should().BeEmpty();
    }

    [Fact]
    public void Round_trip_emits_new_shape_and_drops_legacy_dict()
    {
        // Arrange — load legacy, then re-serialise.
        var legacyJson = """
            {
              "name": "Round trip",
              "connectionKey": "BravoDb",
              "sql": "SELECT 1",
              "expectedColumnValues": { "Col": "v" },
              "timeoutSeconds": 15
            }
            """;
        var def = JsonSerializer.Deserialize<DbCheckStepDefinition>(legacyJson, JsonOpts)!;

        // Act
        var resaved = JsonSerializer.Serialize(def, JsonOpts);

        // Assert — old shape dropped, new shape present.
        resaved.Should().NotContain("expectedColumnValues");
        resaved.Should().Contain("columnAssertions");
        resaved.Should().Contain("\"column\":\"Col\"");
        resaved.Should().Contain("\"expected\":\"v\"");
    }

    [Fact]
    public void New_shape_with_assertions_and_captures_round_trips()
    {
        var def = new DbCheckStepDefinition
        {
            Name = "JSON + capture",
            Sql = "SELECT * FROM Jobs WHERE MessageID = '{{MessageID}}'",
            ColumnAssertions =
            {
                new ColumnAssertion
                {
                    Column = "Payload",
                    JsonPath = "$.OrderId",
                    Operator = AssertionOperator.Equals,
                    Expected = "12345",
                },
                new ColumnAssertion
                {
                    Column = "CreatedAt",
                    Operator = AssertionOperator.EqualsDate,
                    Expected = "2026-05-08T00:00:00Z",
                    ToleranceSeconds = 5,
                },
            },
            Captures =
            {
                new ColumnCapture { Column = "JobId", As = "JobId", Required = true },
            },
        };

        var json = JsonSerializer.Serialize(def, JsonOpts);
        var roundTripped = JsonSerializer.Deserialize<DbCheckStepDefinition>(json, JsonOpts)!;

        roundTripped.ColumnAssertions.Should().HaveCount(2);
        roundTripped.ColumnAssertions[0].JsonPath.Should().Be("$.OrderId");
        roundTripped.ColumnAssertions[1].Operator.Should().Be(AssertionOperator.EqualsDate);
        roundTripped.ColumnAssertions[1].ToleranceSeconds.Should().Be(5);
        roundTripped.Captures.Should().ContainSingle()
            .Which.As.Should().Be("JobId");
    }

    [Fact]
    public void Empty_legacy_dict_does_nothing()
    {
        var legacyJson = """
            { "name": "X", "sql": "SELECT 1", "expectedColumnValues": {}, "timeoutSeconds": 15 }
            """;
        var def = JsonSerializer.Deserialize<DbCheckStepDefinition>(legacyJson, JsonOpts)!;
        def.ColumnAssertions.Should().BeEmpty();
    }

    [Fact]
    public void Operator_serialises_as_string_name()
    {
        var def = new DbCheckStepDefinition
        {
            ColumnAssertions = { new ColumnAssertion { Column = "X", Operator = AssertionOperator.IsNull } },
        };

        var json = JsonSerializer.Serialize(def, JsonOpts);

        // String enum converter on AssertionOperator must round-trip as the bare name.
        json.Should().Contain("\"operator\":\"IsNull\"");
    }
}
