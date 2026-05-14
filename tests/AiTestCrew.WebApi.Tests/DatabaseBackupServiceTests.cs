using System.IO;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Agents.Persistence.Sqlite;
using AiTestCrew.WebApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

/// <summary>
/// Unit and lightweight integration tests for DatabaseBackupService.
/// Uses a real SQLite DB in a temp directory.
/// </summary>
public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _backupDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;

    public DatabaseBackupServiceTests()
    {
        _tmpDir    = Path.Combine(Path.GetTempPath(), $"atc-backup-test-{Guid.NewGuid():N}");
        _backupDir = Path.Combine(_tmpDir, "backups");
        _dbPath    = Path.Combine(_tmpDir, "test.db");
        Directory.CreateDirectory(_tmpDir);
        Directory.CreateDirectory(_backupDir);
        _factory = new SqliteConnectionFactory($"Data Source={_dbPath}");
        using var conn = _factory.CreateConnection(); // prime schema
        _ = conn;
    }

    public void Dispose()
    {
        // Release all pooled SQLite connections so the OS lets us delete the temp dir
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Give GC a chance to finalise any remaining handles
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort; temp-dir cleanup failure doesn't fail the test */ }
    }

    private DatabaseBackupService BuildSvc(BackupConfig? opts = null)
    {
        var cfg = opts ?? new BackupConfig
        {
            Enabled          = true,
            Directory        = _backupDir,
            IntervalMinutes  = 60,
            RetentionHourly  = 24,
            RetentionDaily   = 14,
            RetentionWeekly  = 8,
            MinFreeDiskMb    = 0,
        };
        return new DatabaseBackupService(_factory, cfg, NullLogger<DatabaseBackupService>.Instance);
    }

    // Acceptance #1: Enabled=true -> backup file appears
    [Fact]
    public async Task Enabled_true_produces_backup_file()
    {
        var svc = BuildSvc();
        var result = await svc.RunBackupAsync();

        File.Exists(result.Path).Should().BeTrue();
        result.SizeBytes.Should().BeGreaterThan(0);
        result.DurationMs.Should().BeGreaterOrEqualTo(0);
    }

    // Acceptance #1b: Enabled=false -> hosted service exits without creating files
    [Fact]
    public async Task Enabled_false_no_backup_file_created()
    {
        var cfg = new BackupConfig { Enabled = false, Directory = _backupDir };
        var svc = new DatabaseBackupService(_factory, cfg, NullLogger<DatabaseBackupService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await svc.StartAsync(cts.Token);
        await Task.Delay(200);
        await svc.StopAsync(default);

        Directory.GetFiles(_backupDir, "*.db").Should().BeEmpty();
    }

    // Acceptance #2: Backup is restorable
    [Fact]
    public async Task Backup_is_restorable()
    {
        using (var conn = _factory.CreateConnection())
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO modules (id, name, description, data, created_at, updated_at) VALUES ('restore-test', 'RestoreTest', '', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z')";
            insert.ExecuteNonQuery();
        }

        var svc = BuildSvc();
        var result = await svc.RunBackupAsync();

        using (var conn = _factory.CreateConnection())
        {
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM modules WHERE id = 'restore-test'";
            del.ExecuteNonQuery();
        }

        // Restore: copy backup over live DB using BackupDatabase in reverse
        using (var backupConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={result.Path}"))
        using (var liveConn   = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            backupConn.Open();
            liveConn.Open();
            backupConn.BackupDatabase(liveConn);
        }

        using var verify = _factory.CreateConnection();
        using var sel = verify.CreateCommand();
        sel.CommandText = "SELECT COUNT(*) FROM modules WHERE id = 'restore-test'";
        var count = (long)sel.ExecuteScalar()!;
        count.Should().Be(1, "restored DB should contain the pre-backup module");
    }

    // Acceptance #3: Retention sweep prunes correctly
    [Fact]
    public void Retention_sweep_keeps_correct_files()
    {
        var now = DateTime.UtcNow;
        var seeded = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            var dt   = now.AddHours(-i * 21.6);
            var name = $"aitestcrew-{dt:yyyyMMdd-HHmmss}.db";
            var path = Path.Combine(_backupDir, name);
            File.WriteAllText(path, $"seed-{i}");
            seeded.Add(path);
        }

        var svc = BuildSvc(new BackupConfig
        {
            Enabled         = true,
            Directory       = _backupDir,
            RetentionHourly = 24,
            RetentionDaily  = 14,
            RetentionWeekly = 8,
            MinFreeDiskMb   = 0,
        });
        svc.RunRetentionSweep();

        var survivors = Directory.GetFiles(_backupDir, "aitestcrew-*.db");
        survivors.Length.Should().BeLessOrEqualTo(24 + 14 + 8,
            "hourly(24) + daily(14) + weekly(8) = 46 max");

        var sortedSeeded = seeded
            .OrderByDescending(p => Path.GetFileName(p))
            .Take(24)
            .ToList();
        foreach (var p in sortedSeeded)
            File.Exists(p).Should().BeTrue($"{Path.GetFileName(p)} is in the hourly window");
    }

    // Acceptance #5: Status reflects error on disk-space failure
    [Fact]
    public async Task Status_reflects_error_when_disk_guard_fires()
    {
        var cfg = new BackupConfig
        {
            Enabled       = true,
            Directory     = _backupDir,
            MinFreeDiskMb = int.MaxValue,
        };
        var svc = new DatabaseBackupService(_factory, cfg, NullLogger<DatabaseBackupService>.Instance);

        try { await svc.RunBackupAsync(); }
        catch { /* guard throws */ }

        svc.LastError.Should().NotBeNullOrEmpty();
        svc.LastErrorAt.Should().NotBeNull();
    }

    // Acceptance #6: Disk-space guard skips, does not let file be created
    [Fact]
    public async Task DiskSpaceGuard_skips_and_no_file_created()
    {
        var cfg = new BackupConfig
        {
            Enabled       = true,
            Directory     = _backupDir,
            MinFreeDiskMb = int.MaxValue,
        };
        var svc = new DatabaseBackupService(_factory, cfg, NullLogger<DatabaseBackupService>.Instance);

        Func<Task> act = () => svc.RunBackupAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        Directory.GetFiles(_backupDir, "*.db").Should().BeEmpty("guard must prevent file creation");
    }

    // ParseTimestamp helper tests
    [Theory]
    [InlineData("aitestcrew-20240115-093045.db", 2024, 1, 15, 9, 30, 45)]
    [InlineData("aitestcrew-20231231-235959.db", 2023, 12, 31, 23, 59, 59)]
    public void ParseTimestamp_decodes_correctly(string name, int y, int mo, int d, int h, int mi, int s)
    {
        var ts = DatabaseBackupService.ParseTimestamp(name);
        ts.Should().NotBeNull();
        ts!.Value.Year.Should().Be(y);
        ts!.Value.Month.Should().Be(mo);
        ts!.Value.Day.Should().Be(d);
        ts!.Value.Hour.Should().Be(h);
        ts!.Value.Minute.Should().Be(mi);
        ts!.Value.Second.Should().Be(s);
    }

    [Theory]
    [InlineData("garbage.db")]
    [InlineData("aitestcrew-baddate.db")]
    [InlineData("")]
    public void ParseTimestamp_returns_null_on_bad_input(string name)
    {
        var ts = DatabaseBackupService.ParseTimestamp(name);
        ts.Should().BeNull();
    }
}
