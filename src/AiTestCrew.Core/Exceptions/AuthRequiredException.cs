using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Exceptions;

/// <summary>
/// Thrown by an agent when an authentication failure (401/403, login redirect,
/// expired storage state) cannot be resolved by silent auto-recovery. The orchestrator
/// or queue dispatcher catches this, registers an auth-refresh request scoped to
/// (EnvironmentKey, Surface, ApiStackKey?), and parks the run as AwaitingAuth until
/// the refresh completes — at which point the failing step retries.
/// </summary>
public class AuthRequiredException : Exception
{
    public string EnvironmentKey { get; }
    public AuthSurface Surface { get; }
    public string? ApiStackKey { get; }

    public AuthRequiredException(
        string environmentKey,
        AuthSurface surface,
        string? apiStackKey,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        EnvironmentKey = environmentKey;
        Surface = surface;
        ApiStackKey = apiStackKey;
    }
}
