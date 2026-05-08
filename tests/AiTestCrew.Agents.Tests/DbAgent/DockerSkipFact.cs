using System.Net.Sockets;
using Xunit;

namespace AiTestCrew.Agents.Tests.DbAgent;

/// <summary>
/// xUnit fact that auto-skips when Docker isn't reachable on the host.
/// Used by Testcontainers-backed integration tests so CI hosts without Docker
/// (or developer machines with the daemon stopped) skip cleanly rather than
/// failing the build.
/// </summary>
public sealed class DockerRequiredFactAttribute : FactAttribute
{
    public DockerRequiredFactAttribute()
    {
        if (!IsDockerReachable())
            Skip = "Docker is not reachable — skipping Testcontainers integration test.";
    }

    private static bool IsDockerReachable()
    {
        // Cheap probe: try to connect to the standard Docker daemon TCP port (2375).
        // Falls back to checking the named pipe / socket. We deliberately avoid
        // shelling out to `docker info` because that can hang if the daemon is in
        // a bad state. Testcontainers itself does a richer probe at construction
        // time, so when this preflight passes the test still runs and any deeper
        // failure surfaces normally.
        if (System.Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
            return true;
        if (OperatingSystem.IsWindows())
        {
            // Windows: named-pipe \\.\pipe\docker_engine. Existence is a proxy.
            return File.Exists(@"\\.\pipe\docker_engine") || TryTcpProbe();
        }
        return File.Exists("/var/run/docker.sock") || TryTcpProbe();

        static bool TryTcpProbe()
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync("127.0.0.1", 2375);
                return task.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
            }
            catch { return false; }
        }
    }
}
