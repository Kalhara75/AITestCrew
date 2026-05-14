using AiTestCrew.Agents.Persistence.Sqlite;
using AiTestCrew.Core.Models;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

public class SqliteRunQueueRepositoryRoleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteRunQueueRepository _queueRepo;
    private readonly SqliteAgentRepository _agentRepo;

    public SqliteRunQueueRepositoryRoleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"req010-{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory($"Data Source={_dbPath}");
        _queueRepo = new SqliteRunQueueRepository(_factory);
        _agentRepo = new SqliteAgentRepository(_factory);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect(); GC.WaitForPendingFinalizers();
        try { File.Delete(_dbPath); } catch { }
    }
    private async Task SeedAgentAsync(string id, string role, string[] tags, string[] caps)
    {
        await _agentRepo.UpsertAsync(new Agent
        {
            Id = id,
            Name = $"Agent-{id}",
            Role = role, Tags = tags.ToList(), Capabilities = caps.ToList(), Status = "Online",
        });
    }
    private Task<RunQueueEntry> EnqueueRunAsync(string targetType, string? pin = null, string[]? rtags = null)
    {
        return _queueRepo.EnqueueAsync(new RunQueueEntry {
            ModuleId = "m", TestSetId = "ts", TargetType = targetType,
            JobKind = "Run", Mode = "Normal", RequestJson = "{}",
            PreferredAgentId = pin, RequiredTags = rtags?.ToList() ?? new(), });
    }
    private Task<RunQueueEntry> EnqueueRecordAsync(string t)
    {
        return _queueRepo.EnqueueAsync(new RunQueueEntry {
            ModuleId = "m", TestSetId = "ts", TargetType = t,
            JobKind = "Record", Mode = "Recording", RequestJson = "{}", });
    }

    [Fact]
    public async Task Both_role_claims_Run_jobs()
    {
        await SeedAgentAsync("both-1", "Both", [], ["API"]);
        await EnqueueRunAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("both-1", ["API"]);
        claimed.Should().NotBeNull();
        claimed!.JobKind.Should().Be("Run");
        claimed.ClaimedBy.Should().Be("both-1");
    }

    [Fact]
    public async Task Both_role_claims_Record_jobs()
    {
        await SeedAgentAsync("both-2", "Both", [], ["API"]);
        await EnqueueRecordAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("both-2", ["API"]);
        claimed.Should().NotBeNull();
        claimed!.JobKind.Should().Be("Record");
        claimed.ClaimedBy.Should().Be("both-2");
    }

    [Fact]
    public async Task Execution_role_claims_Run_and_skips_Record()
    {
        await SeedAgentAsync("exec-1", "Execution", [], ["API"]);
        await EnqueueRecordAsync("API");
        await EnqueueRunAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("exec-1", ["API"]);
        claimed.Should().NotBeNull("Execution should skip Record and claim Run");
        claimed!.JobKind.Should().Be("Run");
    }

    [Fact]
    public async Task Execution_role_null_when_only_Record_available()
    {
        await SeedAgentAsync("exec-2", "Execution", [], ["API"]);
        await EnqueueRecordAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("exec-2", ["API"]);
        claimed.Should().BeNull("Execution-only must not claim Record");
    }

    [Fact]
    public async Task Recording_role_claims_Record_and_skips_Run()
    {
        await SeedAgentAsync("rec-1", "Recording", [], ["API"]);
        await EnqueueRunAsync("API");
        await EnqueueRecordAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("rec-1", ["API"]);
        claimed.Should().NotBeNull("Recording should skip Run and claim Record");
        claimed!.JobKind.Should().Be("Record");
    }

    [Fact]
    public async Task Recording_role_null_when_only_Run_available()
    {
        await SeedAgentAsync("rec-2", "Recording", [], ["API"]);
        await EnqueueRunAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("rec-2", ["API"]);
        claimed.Should().BeNull("Recording-only must not claim Run");
    }

    [Fact]
    public async Task Execution_role_skips_AuthSetup_job()
    {
        await SeedAgentAsync("exec-3", "Execution", [], ["API"]);
        await _queueRepo.EnqueueAsync(new RunQueueEntry {
            ModuleId = "m", TestSetId = "ts", TargetType = "API",
            JobKind = "AuthSetup", Mode = "Normal", RequestJson = "{}", });
        var claimed = await _queueRepo.ClaimNextAsync("exec-3", ["API"]);
        claimed.Should().BeNull();
    }

    [Fact]
    public async Task Pinned_job_claimed_only_by_preferred_agent()
    {
        await SeedAgentAsync("preferred", "Both", [], ["API"]);
        await SeedAgentAsync("other", "Both", [], ["API"]);
        var entry = await EnqueueRunAsync("API", pin: "preferred");
        var notClaimed = await _queueRepo.ClaimNextAsync("other", ["API"]);
        notClaimed.Should().BeNull("pinned must not be claimed by non-preferred agent");
        var claimed = await _queueRepo.ClaimNextAsync("preferred", ["API"]);
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(entry.Id);
        claimed.ClaimedBy.Should().Be("preferred");
    }

    [Fact]
    public async Task Preferred_agent_claims_unpinned_jobs()
    {
        await SeedAgentAsync("pref2", "Both", [], ["API"]);
        var entry = await EnqueueRunAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("pref2", ["API"]);
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task Required_tags_not_claimed_by_agent_without_tags()
    {
        await SeedAgentAsync("no-tags", "Both", [], ["API"]);
        await EnqueueRunAsync("API", rtags: ["env:prod"]);
        var claimed = await _queueRepo.ClaimNextAsync("no-tags", ["API"]);
        claimed.Should().BeNull("agent without tags must not claim");
    }

    [Fact]
    public async Task Required_tags_claimed_by_agent_with_superset()
    {
        await SeedAgentAsync("tagged", "Both", ["env:prod", "region:au"], ["API"]);
        var entry = await EnqueueRunAsync("API", rtags: ["env:prod"]);
        var claimed = await _queueRepo.ClaimNextAsync("tagged", ["API"]);
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task No_required_tags_claimed_by_any_agent()
    {
        await SeedAgentAsync("bare", "Both", [], ["API"]);
        var entry = await EnqueueRunAsync("API");
        var claimed = await _queueRepo.ClaimNextAsync("bare", ["API"]);
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task Second_tagged_agent_claims_job_skipped_by_untagged()
    {
        await SeedAgentAsync("bare2", "Both", [], ["API"]);
        await SeedAgentAsync("tagged2", "Both", ["env:prod"], ["API"]);
        var entry = await EnqueueRunAsync("API", rtags: ["env:prod"]);
        var miss = await _queueRepo.ClaimNextAsync("bare2", ["API"]);
        miss.Should().BeNull();
        var claimed = await _queueRepo.ClaimNextAsync("tagged2", ["API"]);
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task ExpireUnclaimed_marks_old_entries_as_Failed()
    {
        var entry = await EnqueueRunAsync("API");
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE run_queue SET created_at = '2000-01-01T00:00:00.0000000Z' WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", entry.Id);
        await cmd.ExecuteNonQueryAsync();
        int expired = await _queueRepo.ExpireUnclaimedAsync(
            DateTime.UtcNow.AddSeconds(-10), "No agent claimed within deadline");
        expired.Should().Be(1);
        var updated = await _queueRepo.GetByIdAsync(entry.Id);
        updated!.Status.Should().Be("Failed");
        updated.Error.Should().Contain("No agent claimed");
    }
}
