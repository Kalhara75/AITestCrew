using System.Net;
using System.Net.Http.Json;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.WebApi.Endpoints;
using AiTestCrew.WebApi.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

/// <summary>
/// Integration tests for <c>POST /api/db-check/dry-run</c> + <c>GET /api/db-check/connections</c>.
/// Spins up a minimal in-memory host with only the DI required by the endpoints —
/// avoids dragging in the full WebApi pipeline (LLM keys, persistence, etc.).
///
/// The "happy path" is covered by the integration test in AiTestCrew.Agents.Tests
/// (Testcontainers SQL Server). These tests assert the endpoint's gating logic:
/// guardrail rejections, rate-limit, env opt-out, and unknown-key 404.
/// </summary>
public class DbCheckEndpointsTests
{
    [Fact]
    public async Task Connections_lists_BravoDb_and_configured_keys()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["SdrReportingDb"] = "Server=x;" },
        };
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.GetAsync("/api/db-check/connections?envKey=default");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<KeysResponse>();
        body!.Keys.Should().Contain(new[] { "BravoDb", "SdrReportingDb" });
    }

    [Fact]
    public async Task DryRun_rejects_non_select_via_guardrail()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["BravoDb"] = "Server=x;Database=y;" },
        };
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/db-check/dry-run", new
        {
            envKey = (string?)null,
            connectionKey = "BravoDb",
            sql = "DROP TABLE Jobs",
            parameters = new Dictionary<string, string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DryRun_returns_404_on_unknown_connection()
    {
        var cfg = new TestEnvironmentConfig();  // no DbConnections at all
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/db-check/dry-run", new
        {
            envKey = (string?)null,
            connectionKey = "MissingDb",
            sql = "SELECT 1",
            parameters = new Dictionary<string, string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DryRun_returns_403_when_env_opts_out()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["BravoDb"] = "Server=x;Database=y;" },
            Environments =
            {
                ["prod"] = new EnvironmentConfig { AllowDbDryRun = false },
            },
        };
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/db-check/dry-run", new
        {
            envKey = "prod",
            connectionKey = "BravoDb",
            sql = "SELECT 1",
            parameters = new Dictionary<string, string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DryRun_rate_limits_after_threshold_per_user()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["BravoDb"] = "Server=x;Database=y;" },
        };
        // Tighten the limit so the test runs fast and deterministically.
        using var host = await BuildHostAsync(cfg, new DbDryRunRateLimiter(maxPerWindow: 2, window: TimeSpan.FromMinutes(1)));
        var client = host.GetTestClient();

        // Use a SQL that fails AT the connection (Server=x is invalid) — but
        // the rate limiter fires BEFORE that, so requests #1 and #2 will return
        // 500 (connection failure) and request #3 will return 429.
        async Task<HttpStatusCode> Hit() => (await client.PostAsJsonAsync("/api/db-check/dry-run", new
        {
            envKey = (string?)null,
            connectionKey = "BravoDb",
            sql = "SELECT 1",
            parameters = new Dictionary<string, string>(),
        })).StatusCode;

        var first = await Hit();
        var second = await Hit();
        var third = await Hit();

        first.Should().NotBe(HttpStatusCode.TooManyRequests);
        second.Should().NotBe(HttpStatusCode.TooManyRequests);
        third.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task DryRun_substitutes_parameters_into_sql_before_guardrail()
    {
        // If parameter substitution introduces a denied keyword, the guardrail
        // catches it. Locks the order: substitute → validate → execute.
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["BravoDb"] = "Server=x;Database=y;" },
        };
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/db-check/dry-run", new
        {
            envKey = (string?)null,
            connectionKey = "BravoDb",
            sql = "{{Bad}} FROM Jobs",
            parameters = new Dictionary<string, string> { ["Bad"] = "DELETE" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<IHost> BuildHostAsync(
        TestEnvironmentConfig cfg,
        DbDryRunRateLimiter? limiter = null)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(cfg);
                        services.AddSingleton<IEnvironmentResolver>(_ => new EnvironmentResolver(cfg));
                        services.AddSingleton(limiter ?? new DbDryRunRateLimiter());
                        services.AddLogging();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e =>
                        {
                            e.MapGroup("/api/db-check").MapDbCheckEndpoints();
                        });
                    });
            });
        var host = await hostBuilder.StartAsync();
        return host;
    }

    private record KeysResponse(IReadOnlyList<string> Keys);
}
