namespace AiTestCrew.Core.Exceptions;

/// <summary>
/// Thrown when a versioned write fails because the stored version has advanced
/// beyond the caller's expected version (optimistic concurrency conflict).
/// </summary>
public sealed class ConcurrencyException : Exception
{
    public int CurrentVersion { get; }
    public int YourVersion { get; }
    public string? CurrentUpdatedBy { get; }
    public string? CurrentUpdatedAt { get; }

    public ConcurrencyException(int currentVersion, int yourVersion, string? updatedBy, string? updatedAt)
        : base($"Concurrency conflict: current version is {currentVersion}, your version is {yourVersion}")
    {
        CurrentVersion = currentVersion;
        YourVersion = yourVersion;
        CurrentUpdatedBy = updatedBy;
        CurrentUpdatedAt = updatedAt;
    }
}
