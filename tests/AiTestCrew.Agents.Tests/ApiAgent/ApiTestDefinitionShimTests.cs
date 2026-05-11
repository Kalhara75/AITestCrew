using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.DbAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.ApiAgent;

/// <summary>
/// Covers <see cref="ApiTestDefinition.NormaliseLegacyFields"/> — the one-way
/// shim that promotes legacy ExpectedStatus/ExpectedBodyContains/
/// ExpectedBodyNotContains into typed <see cref="ApiAssertion"/> entries.
/// </summary>
public class ApiTestDefinitionShimTests
{
    [Fact]
    public void No_promotion_when_defaults_and_ApiAssertions_empty()
    {
        var def = new ApiTestDefinition(); // ExpectedStatus=200, lists empty
        def.NormaliseLegacyFields();
        def.ApiAssertions.Should().BeEmpty();
    }

    [Fact]
    public void Promotes_non_200_status_into_Status_Equals_assertion()
    {
        var def = new ApiTestDefinition { ExpectedStatus = 201 };
        def.NormaliseLegacyFields();
        def.ApiAssertions.Should().ContainSingle(a =>
            a.Source == ApiAssertionSource.Status &&
            a.Operator == AssertionOperator.Equals &&
            a.Expected == "201");
    }

    [Fact]
    public void Promotes_body_contains_into_BodyText_Contains_assertion()
    {
        var def = new ApiTestDefinition { ExpectedBodyContains = { "hello" } };
        def.NormaliseLegacyFields();
        def.ApiAssertions.Should().ContainSingle(a =>
            a.Source == ApiAssertionSource.BodyText &&
            a.Operator == AssertionOperator.Contains &&
            a.Expected == "hello" &&
            a.IgnoreCase);
    }

    [Fact]
    public void Promotes_body_not_contains_into_BodyText_NotContains_assertion()
    {
        var def = new ApiTestDefinition { ExpectedBodyNotContains = { "error" } };
        def.NormaliseLegacyFields();
        def.ApiAssertions.Should().ContainSingle(a =>
            a.Source == ApiAssertionSource.BodyText &&
            a.Operator == AssertionOperator.NotContains &&
            a.Expected == "error");
    }

    [Fact]
    public void Promotes_all_three_legacy_fields_in_order()
    {
        var def = new ApiTestDefinition
        {
            ExpectedStatus = 404,
            ExpectedBodyContains = { "not found" },
            ExpectedBodyNotContains = { "stack trace" }
        };
        def.NormaliseLegacyFields();
        def.ApiAssertions.Should().HaveCount(3);
        def.ApiAssertions[0].Source.Should().Be(ApiAssertionSource.Status);
        def.ApiAssertions[1].Operator.Should().Be(AssertionOperator.Contains);
        def.ApiAssertions[2].Operator.Should().Be(AssertionOperator.NotContains);
    }

    [Fact]
    public void Is_idempotent_when_ApiAssertions_already_populated()
    {
        var def = new ApiTestDefinition
        {
            ExpectedStatus = 404,
            ApiAssertions = { new ApiAssertion { Source = ApiAssertionSource.Status, Expected = "200" } }
        };
        def.NormaliseLegacyFields();
        // Must not add a second assertion — existing ApiAssertions are authoritative.
        def.ApiAssertions.Should().ContainSingle();
        def.ApiAssertions[0].Expected.Should().Be("200");
    }

    [Fact]
    public void Status_200_with_empty_body_lists_is_a_no_op()
    {
        // Normal-mode: let the LLM validator run — don't inject a status assertion.
        var def = new ApiTestDefinition { ExpectedStatus = 200 };
        def.NormaliseLegacyFields();
        def.ApiAssertions.Should().BeEmpty("status=200 with no body lists is the default; LLM should handle it");
    }
}
