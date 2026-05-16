using AiTestCrew.Agents.Persistence.Sqlite;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

/// <summary>
/// SQLite repository tests for user role assignment + last-admin guard.
/// Tests the persistence layer; endpoint-level authorisation is covered
/// by integration tests that bring up a real TestHost (deferred to Phase 2).
/// </summary>
public class UserEndpointsRoleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteUserRepository _repo;

    public UserEndpointsRoleTests()
    {
        _dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"req012-users-{System.Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory($"Data Source={_dbPath}");
        _repo = new SqliteUserRepository(_factory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        System.GC.Collect(); System.GC.WaitForPendingFinalizers();
        try { System.IO.File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task First_user_is_promoted_to_Admin()
    {
        var u1 = await _repo.CreateAsync("Alice");
        u1.Role.Should().Be("Admin");
    }

    [Fact]
    public async Task Subsequent_users_default_to_User_role()
    {
        await _repo.CreateAsync("Alice"); // admin
        var u2 = await _repo.CreateAsync("Bob");
        u2.Role.Should().Be("User");
    }

    [Fact]
    public async Task SetRoleAsync_updates_role()
    {
        var u = await _repo.CreateAsync("Alice");
        await _repo.SetRoleAsync(u.Id, "AuthSteward");
        var updated = await _repo.GetByIdAsync(u.Id);
        updated!.Role.Should().Be("AuthSteward");
    }

    [Fact]
    public async Task CountAdminsAsync_counts_active_admins()
    {
        var u1 = await _repo.CreateAsync("Alice"); // admin
        var count1 = await _repo.CountAdminsAsync();
        count1.Should().Be(1);

        await _repo.SetRoleAsync(u1.Id, "User");
        var count2 = await _repo.CountAdminsAsync();
        count2.Should().Be(0);
    }

    [Fact]
    public async Task GetByApiKey_returns_role()
    {
        var u = await _repo.CreateAsync("Alice");
        var fetched = await _repo.GetByApiKeyAsync(u.ApiKey);
        fetched!.Role.Should().Be("Admin");
    }
}
