using AiTestCrew.Agents.Persistence.Sqlite;
using AiTestCrew.Core.Models;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

/// <summary>
/// REQ-004 §10 / Phase 9 — proves the run-queue claim path routes
/// `Event_AzureServiceBus` entries to agents that advertise the matching
/// capability string and refuses entries to agents that don't. Capabilities
/// are free-form strings (no enum); the claim's <c>target_type IN (...)</c>
/// predicate is the only routing primitive, so a regression here would
/// silently strand event-assert post-steps.
/// </summary>
public class EventAssertQueueRoutingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteRunQueueRepository _repo;

    public EventAssertQueueRoutingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"req004-queue-{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory($"Data Source={_dbPath}");
        _repo = new SqliteRunQueueRepository(_factory);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Event_AzureServiceBus_entry_is_claimable_by_matching_agent()
    {
        var enqueued = await _repo.EnqueueAsync(new RunQueueEntry
        {
            ModuleId = "m", TestSetId = "ts", ObjectiveId = "o",
            TargetType = "Event_AzureServiceBus",
            JobKind = "Run",
            Mode = "VerifyOnly",
            RequestJson = "{}",
        });

        var claimed = await _repo.ClaimNextAsync(
            "agent-1", new[] { "Event_AzureServiceBus" });

        claimed.Should().NotBeNull("an agent advertising Event_AzureServiceBus must be able to claim the entry");
        claimed!.Id.Should().Be(enqueued.Id);
        claimed.TargetType.Should().Be("Event_AzureServiceBus");
        claimed.Status.Should().Be("Claimed");
    }

    [Fact]
    public async Task Event_AzureServiceBus_entry_is_NOT_claimable_by_agent_without_capability()
    {
        await _repo.EnqueueAsync(new RunQueueEntry
        {
            ModuleId = "m", TestSetId = "ts", ObjectiveId = "o",
            TargetType = "Event_AzureServiceBus",
            JobKind = "Run",
            Mode = "VerifyOnly",
            RequestJson = "{}",
        });

        // Agent only advertises the default UI / DB caps — must NOT pick up Event_AzureServiceBus.
        var claimed = await _repo.ClaimNextAsync(
            "agent-2",
            new[] { "UI_Web_Blazor", "UI_Web_MVC", "UI_Desktop_WinForms", "Db_SqlServer" });

        claimed.Should().BeNull(
            "the queue's claim predicate matches target_type literally — an agent that doesn't advertise Event_AzureServiceBus must be invisible to the entry");
    }

    [Fact]
    public async Task Mixed_capabilities_only_pick_up_matching_targets()
    {
        // Two entries, two target types. An agent advertising both caps gets
        // them in FIFO order; an agent advertising only one cap skips the other.
        await _repo.EnqueueAsync(new RunQueueEntry
        {
            ModuleId = "m", TestSetId = "ts", ObjectiveId = "o1",
            TargetType = "Event_AzureServiceBus",
            JobKind = "Run", Mode = "VerifyOnly", RequestJson = "{}",
        });
        await _repo.EnqueueAsync(new RunQueueEntry
        {
            ModuleId = "m", TestSetId = "ts", ObjectiveId = "o2",
            TargetType = "Db_SqlServer",
            JobKind = "Run", Mode = "VerifyOnly", RequestJson = "{}",
        });

        // Agent #1 only does DB — should get the DB entry, not the event one.
        var dbAgent = await _repo.ClaimNextAsync("agent-db", new[] { "Db_SqlServer" });
        dbAgent.Should().NotBeNull();
        dbAgent!.TargetType.Should().Be("Db_SqlServer");

        // Agent #2 only does events — should get the event entry that's still queued.
        var evtAgent = await _repo.ClaimNextAsync("agent-evt", new[] { "Event_AzureServiceBus" });
        evtAgent.Should().NotBeNull();
        evtAgent!.TargetType.Should().Be("Event_AzureServiceBus");

        // Nothing left for either.
        (await _repo.ClaimNextAsync("agent-db", new[] { "Db_SqlServer" })).Should().BeNull();
        (await _repo.ClaimNextAsync("agent-evt", new[] { "Event_AzureServiceBus" })).Should().BeNull();
    }

    [Fact]
    public async Task NotBeforeAt_in_future_blocks_claim_for_event_entry()
    {
        // Sanity: the existing not_before_at gate (used by deferred post-step
        // queueing) still applies to event-assert entries.
        await _repo.EnqueueAsync(new RunQueueEntry
        {
            ModuleId = "m", TestSetId = "ts", ObjectiveId = "o",
            TargetType = "Event_AzureServiceBus",
            JobKind = "Run", Mode = "VerifyOnly", RequestJson = "{}",
            NotBeforeAt = DateTime.UtcNow.AddMinutes(5),
        });

        var claimed = await _repo.ClaimNextAsync(
            "agent-3", new[] { "Event_AzureServiceBus" });
        claimed.Should().BeNull("not_before_at is 5 minutes in the future");
    }
}
