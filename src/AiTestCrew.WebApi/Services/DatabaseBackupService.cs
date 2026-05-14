using System.IO;
using AiTestCrew.Agents.Persistence.Sqlite;
using AiTestCrew.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Background service that runs SQLite Online Backup API on a configurable cadence.
/// Writes timestamped snapshots and prunes via tiered hourly/daily/weekly retention.
/// The backup directory MUST be a separate host bind-mount (not inside the data volume).
/// Mirrors AgentHeartbeatMonitor PeriodicTimer shape. Each tick is isolated by try/catch.
/// </summary>
public sealed class DatabaseBackupService : BackgroundService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly BackupConfig _opts;
    private readonly ILogger<DatabaseBackupService> _logger;

    private DateTime? _lastSuccessAt;
    private long _lastSuccessSizeBytes;
    private DateTime? _lastErrorAt;
    private string? _lastError;
    private DateTime? _nextScheduledAt;
    private int _running;

    public DatabaseBackupService(
        SqliteConnectionFactory factory,
        BackupConfig opts,
        ILogger<DatabaseBackupService> logger)
    {
        _factory = factory;
        _opts = opts;
        _logger = logger;
    }

    public DateTime? LastSuccessAt => _lastSuccessAt;
    public long LastSuccessSizeBytes => _lastSuccessSizeBytes;
    public DateTime? LastErrorAt => _lastErrorAt;
    public string? LastError => _lastError;
    public DateTime? NextScheduledAt => _nextScheduledAt;
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public BackupStatusDto GetStatus()
    {
        var files = SafeListFiles();
        return new BackupStatusDto
        {
            Enabled = _opts.Enabled,
            LastSuccessAt = _lastSuccessAt,
            LastSuccessSizeBytes = _lastSuccessSizeBytes,
            LastErrorAt = _lastErrorAt,
            LastError = _lastError,
            NextScheduledAt = _nextScheduledAt,
            TotalBackupsOnDisk = files.Count,
            OldestBackupAt = files.Count > 0 ? ParseTimestamp(files.Min(f => f.Name) ?? string.Empty) : null,
        };
    }

    public IReadOnlyList<string> ListBackupPaths()
        => SafeListFiles().Select(f => f.FullName).ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _logger.LogInformation("DatabaseBackupService: disabled. No backups will run.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _opts.IntervalMinutes));
        _logger.LogInformation(
            "DatabaseBackupService starting -- interval {Minutes} min, directory {Dir}",
            interval.TotalMinutes, _opts.Directory);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            _nextScheduledAt = DateTime.UtcNow.Add(interval);
            await TickAsync();
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<BackupResultDto> RunBackupAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("A backup is already in progress.");
        try { return await DoBackupAsync(ct); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private async Task TickAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try { await DoBackupAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "DatabaseBackupService: tick failed"); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private async Task<BackupResultDto> DoBackupAsync(CancellationToken ct)
    {
        EnsureDirectory();

        if (_opts.MinFreeDiskMb > 0 && !HasEnoughDiskSpace(_opts.Directory, _opts.MinFreeDiskMb))
        {
            var msg = $"Skipping backup -- free disk below {_opts.MinFreeDiskMb} MB threshold.";
            _logger.LogWarning("DatabaseBackupService: {Message}", msg);
            _lastErrorAt = DateTime.UtcNow;
            _lastError = msg;
            throw new InvalidOperationException(msg);
        }

        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var dest = Path.Combine(_opts.Directory, $"aitestcrew-{ts}.db");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await Task.Run(() =>
            {
                using var src = _factory.CreateConnection();
                using var dst = new SqliteConnection($"Data Source={dest}");
                dst.Open();
                src.BackupDatabase(dst);
                dst.Close();
            }, ct);

            sw.Stop();
            var size = new FileInfo(dest).Length;
            _lastSuccessAt = DateTime.UtcNow;
            _lastSuccessSizeBytes = size;
            _lastError = null;
            _logger.LogInformation(
                "DatabaseBackupService: backup done -- {Path} ({Size:N0} bytes, {Ms} ms)",
                dest, size, sw.ElapsedMilliseconds);
            RunRetentionSweep();
            return new BackupResultDto { Path = dest, SizeBytes = size, DurationMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _lastErrorAt = DateTime.UtcNow;
            _lastError = ex.Message;
            _logger.LogError(ex, "DatabaseBackupService: backup failed");
            throw;
        }
    }

    public void RunRetentionSweep()
    {
        try
        {
            var files = SafeListFiles();
            if (files.Count == 0) return;

            files.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.Ordinal));
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files.Take(_opts.RetentionHourly))
                keep.Add(f.FullName);

            var seenDays = new HashSet<string>();
            var dailyCount = 0;
            foreach (var f in files.Skip(_opts.RetentionHourly))
            {
                var pt = ParseTimestamp(f.Name);
                if (pt is null) continue;
                var dayKey = pt.Value.ToString("yyyyMMdd");
                if (seenDays.Add(dayKey))
                {
                    keep.Add(f.FullName);
                    if (++dailyCount >= _opts.RetentionDaily) break;
                }
            }

            var seenWeeks = new HashSet<string>();
            var weeklyCount = 0;
            foreach (var f in files.Skip(_opts.RetentionHourly).Where(f => !keep.Contains(f.FullName)))
            {
                var pt = ParseTimestamp(f.Name);
                if (pt is null) continue;
                var dow = (int)pt.Value.DayOfWeek;
                var daysFromMon = dow == 0 ? 6 : dow - 1;
                var weekKey = pt.Value.AddDays(-daysFromMon).Date.ToString("yyyyMMdd");
                if (seenWeeks.Add(weekKey))
                {
                    keep.Add(f.FullName);
                    if (++weeklyCount >= _opts.RetentionWeekly) break;
                }
            }

            var deleted = 0;
            foreach (var f in files)
            {
                if (keep.Contains(f.FullName)) continue;
                try { File.Delete(f.FullName); deleted++; }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete {Path}", f.FullName); }
            }

            if (deleted > 0)
                _logger.LogInformation(
                    "DatabaseBackupService: retention deleted {Count} file(s), {Kept} kept.",
                    deleted, keep.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DatabaseBackupService: retention sweep failed");
        }
    }

    private void EnsureDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_opts.Directory))
            Directory.CreateDirectory(_opts.Directory);
    }

    private List<FileInfo> SafeListFiles()
    {
        try
        {
            if (!Directory.Exists(_opts.Directory)) return [];
            return [.. new DirectoryInfo(_opts.Directory).GetFiles("aitestcrew-*.db")];
        }
        catch { return []; }
    }

    private static bool HasEnoughDiskSpace(string directory, int minFreeMb)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (root is null) return true;
            return new DriveInfo(root).AvailableFreeSpace >= (long)minFreeMb * 1024 * 1024;
        }
        catch { return true; }
    }

    public static DateTime? ParseTimestamp(string fileName)
    {
        fileName = Path.GetFileNameWithoutExtension(fileName);
        const string prefix = "aitestcrew-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var tsPart = fileName[prefix.Length..];
        if (DateTime.TryParseExact(tsPart, "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return dt;
        return null;
    }
}

public record BackupStatusDto
{
    public bool Enabled { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public long LastSuccessSizeBytes { get; init; }
    public DateTime? LastErrorAt { get; init; }
    public string? LastError { get; init; }
    public DateTime? NextScheduledAt { get; init; }
    public int TotalBackupsOnDisk { get; init; }
    public DateTime? OldestBackupAt { get; init; }
}

public record BackupResultDto
{
    public string Path { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long DurationMs { get; init; }
}
