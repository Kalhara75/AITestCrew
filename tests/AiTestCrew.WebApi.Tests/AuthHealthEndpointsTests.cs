using AiTestCrew.Core.Models;
using AiTestCrew.WebApi.Endpoints;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

/// <summary>
/// Unit tests for AuthHealthEndpoints.IsVisibleToUser scoping logic (REQ-012).
/// </summary>
public class AuthHealthEndpointsTests
{
    private static Agent PersonalAgent(string id, string userId) => new()
    {
        Id = id, Name = id, UserId = userId,
        Status = "Online", IsShared = false,
    };

    private static Agent SharedAgent(string id, string userId) => new()
    {
        Id = id, Name = id, UserId = userId,
        Status = "Online", IsShared = true,
    };

    private static User UserWithRole(string id, string role) => new()
    {
        Id = id, Name = id, ApiKey = "key", Role = role, IsActive = true,
    };

    [Fact]
    public void NullUser_sees_all_agents()
    {
        var agent = PersonalAgent("a1", "owner");
        AuthHealthEndpoints.IsVisibleToUser(agent, null).Should().BeTrue();
    }

    [Fact]
    public void User_role_sees_own_agents_only()
    {
        var me = UserWithRole("u1", "User");
        var own = PersonalAgent("a1", "u1");
        var other = PersonalAgent("a2", "u2");
        var shared = SharedAgent("a3", "admin");

        AuthHealthEndpoints.IsVisibleToUser(own, me).Should().BeTrue("own agent should be visible");
        AuthHealthEndpoints.IsVisibleToUser(other, me).Should().BeFalse("other user agent should not be visible");
        AuthHealthEndpoints.IsVisibleToUser(shared, me).Should().BeFalse("shared agent should not be visible to plain User");
    }

    [Fact]
    public void AuthSteward_sees_own_and_shared_agents()
    {
        var me = UserWithRole("u1", "AuthSteward");
        var own = PersonalAgent("a1", "u1");
        var other = PersonalAgent("a2", "u2");
        var shared = SharedAgent("a3", "admin");

        AuthHealthEndpoints.IsVisibleToUser(own, me).Should().BeTrue("own agent visible to AuthSteward");
        AuthHealthEndpoints.IsVisibleToUser(other, me).Should().BeFalse("other user personal agent not visible");
        AuthHealthEndpoints.IsVisibleToUser(shared, me).Should().BeTrue("shared agent visible to AuthSteward");
    }

    [Fact]
    public void Admin_sees_all_agents()
    {
        var me = UserWithRole("admin", "Admin");
        var own = PersonalAgent("a1", "admin");
        var other = PersonalAgent("a2", "u2");
        var shared = SharedAgent("a3", "u3");

        AuthHealthEndpoints.IsVisibleToUser(own, me).Should().BeTrue();
        AuthHealthEndpoints.IsVisibleToUser(other, me).Should().BeTrue();
        AuthHealthEndpoints.IsVisibleToUser(shared, me).Should().BeTrue();
    }
}
