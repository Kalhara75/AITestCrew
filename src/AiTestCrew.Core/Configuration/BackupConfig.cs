namespace AiTestCrew.Core.Configuration;

/// <summary>
/// Configuration for the scheduled SQLite hot-backup service.
/// Bound from appsettings.json -> "TestEnvironment.Backup".
/// </summary>
public class BackupConfig
{
    /// <summary>
    /// When true, the <c>DatabaseBackupService</c> takes periodic snapshots.
    /// Disabled by default — opt-in in your production config.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Absolute path on the container (or host) where backup files are written.
    /// MUST be a separate host bind-mount — NOT a path inside the named data volume.
    /// Windows containers: <c>c:\backups</c>; Linux containers: <c>/backups</c>.
    /// The directory is created automatically if it does not exist.
    /// </summary>
    public string Directory { get; set; } = @"c:\backups";

    /// <summary>Cadence between automatic backups, in minutes. Default: 30.</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Number of most-recent hourly snapshots to retain.
    /// These are the newest N files regardless of calendar boundary. Default: 24.
    /// </summary>
    public int RetentionHourly { get; set; } = 24;

    /// <summary>
    /// Beyond the hourly window, keep one file per UTC calendar day for this many days. Default: 14.
    /// </summary>
    public int RetentionDaily { get; set; } = 14;

    /// <summary>
    /// Beyond the daily window, keep one file per UTC week (Monday boundary) for this many weeks. Default: 8.
    /// </summary>
    public int RetentionWeekly { get; set; } = 8;

    /// <summary>
    /// Minimum free disk space (MB) required before a backup is attempted.
    /// When free space falls below this threshold the backup is skipped and a warning is logged.
    /// Default: 500 MB. Set to 0 to disable the guard.
    /// </summary>
    public int MinFreeDiskMb { get; set; } = 500;
}
