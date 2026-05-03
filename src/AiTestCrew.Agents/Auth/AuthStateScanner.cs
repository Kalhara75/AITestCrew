using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.Auth;

/// <summary>
/// Walks every configured env × surface pair and stats the resolved cached
/// storage-state file, producing one <see cref="AgentAuthState"/> entry per
/// (env, surface) where the env has <c>AuthHealthEnabled</c> set. Used by the
/// agent's heartbeat loop to feed the dashboard's pre-flight auth-health
/// panel.
///
/// Envs with <c>AuthHealthEnabled = false</c> are skipped — the dashboard
/// won't surface tiles for them, and historical rows for those envs are
/// pruned because the heartbeat replaces the agent's rows wholesale.
///
/// Read-only — only calls <see cref="File.GetLastWriteTimeUtc"/> on existing
/// files; never opens or locks them, so the scanner never contends with a
/// concurrent Playwright session writing the file.
/// </summary>
public sealed class AuthStateScanner
{
    private readonly IEnvironmentResolver _envResolver;

    public AuthStateScanner(IEnvironmentResolver envResolver) => _envResolver = envResolver;

    public IReadOnlyList<AgentAuthState> Scan()
    {
        var results = new List<AgentAuthState>();
        foreach (var key in _envResolver.ListKeys())
        {
            // Per-env opt-out — keep envs the user has explicitly hidden out
            // of agent_auth_state entirely so the dashboard never offers a
            // refresh for them.
            if (!_envResolver.ResolveAuthHealthEnabled(key)) continue;

            AppendIfTracked(results, key, AuthSurface.WebBlazor,
                _envResolver.ResolveBraveCloudUiStorageStatePath(key));
            AppendIfTracked(results, key, AuthSurface.WebMvc,
                _envResolver.ResolveLegacyWebUiStorageStatePath(key));
        }
        return results;
    }

    private static void AppendIfTracked(
        List<AgentAuthState> sink, string envKey, AuthSurface surface, string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;

        var fullPath = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.Combine(AppContext.BaseDirectory, rawPath);

        var exists = File.Exists(fullPath);
        DateTime? mtime = null;
        if (exists)
        {
            try { mtime = File.GetLastWriteTimeUtc(fullPath); }
            catch { exists = false; }
        }

        sink.Add(new AgentAuthState
        {
            EnvironmentKey = envKey,
            Surface = surface,
            FileExists = exists,
            FileMtimeUtc = mtime,
        });
    }
}
