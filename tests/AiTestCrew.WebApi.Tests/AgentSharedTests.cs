using AiTestCrew.Agents.Persistence.Sqlite;
using AiTestCrew.Core.Models;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

/// <summary>
/// Tests for Agent.IsShared persistence and UpsertAsync behaviour (REQ-012).
/// </summary>
public class AgentSharedTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteAgentRepository _repo;

    public AgentSharedTests()
    {
        _dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"req012-agents-{System.Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory($"Data Source={_dbPath}");
        _repo = new SqliteAgentRepository(_factory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        System.GC.Collect(); System.GC.WaitForPendingFinalizers();
        try { System.IO.File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Agent_defaults_to_not_shared()
    {
        await _repo.UpsertAsync(new Agent { Id = "a1", Name = "a1", Status = "Online", IsShared = false });
        var a = await _repo.GetByIdAsync("a1");
        a!.IsShared.Should().BeFalse();
    }

    [Fact]
    public async Task Agent_persists_shared_true()
    {
        await _repo.UpsertAsync(new Agent { Id = "a1", Name = "CI-VM", Status = "Online", IsShared = true });
        var a = await _repo.GetByIdAsync("a1");
        a!.IsShared.Should().BeTrue();
    }

    [Fact]
    public async Task SetSharedAsync_updates_flag()
    {
        await _repo.UpsertAsync(new Agent { Id = "a1", Name = "a1", Status = "Online" });
        await _repo.SetSharedAsync("a1", true);
        var a = await _repo.GetByIdAsync("a1");
        a!.IsShared.Should().BeTrue();
    }

    [Fact]
    public async Task Re_registration_preserves_shared_flag()
    {
        // Register shared via explicit flag
        await _repo.UpsertAsync(new Agent { Id = "a1", Name = "CI-VM", Status = "Online", IsShared = true });
        // Re-register without the shared flag (runner restart without --shared)
        await _repo.UpsertAsync(new Agent { Id = "a1", Name = "CI-VM", Status = "Online", IsShared = false });
        var a = await _repo.GetByIdAsync("a1");
        // CASE: is_shared = CASE WHEN excluded.is_shared = 1 THEN 1 ELSE is_shared END
        // Re-registration with IsShared=false should NOT clear a previously-set true.
        a!.IsShared.Should().BeTrue("is_shared should be sticky once set via admin");
    }

    [Fact]
    public async Task Re_registration_sets_shared_when_flag_present()
    {
        // Start personal, then re-register as shared (admin flow)
        await _repo.UpsertAsync(new Agent { Id = "a1", Name = "a1", Status = "Online", IsShared = false });
        await _repo.UpsertAsync(new Agent { Id = "a1", Name = "a1", Status = "Online", IsShared = true });
        var a = await _repo.GetByIdAsync("a1");
        a!.IsShared.Should().BeTrue();
    }
}
