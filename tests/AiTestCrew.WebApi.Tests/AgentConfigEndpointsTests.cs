using System.Net;
using System.Net.Http.Json;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.WebApi.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

public class AgentConfigEndpointsTests
{
    [Fact]
    public async Task Db_returns_200_with_connection_string_on_happy_path()
    {
        var stub = new FakeEnvResolver();
        stub.AllowResolution["sumo-retail"] = true;
        stub.DbConnections[("BravoDb", "sumo-retail")] = "Server=db;Database=bravo;";

        using var host = await BuildHostAsync(stub, authenticated: true);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/db/BravoDb");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DbConnResponse>();
        body!.ConnectionString.Should().Be("Server=db;Database=bravo;");
        body.ConnectionKey.Should().Be("BravoDb");
    }

    [Fact]
    public async Task Db_returns_401_when_unauthenticated()
    {
        using var host = await BuildHostAsync(new FakeEnvResolver(), authenticated: false);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/db/BravoDb");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Db_returns_403_when_AllowAgentConnectionResolution_is_false()
    {
        var stub = new FakeEnvResolver();
        stub.AllowResolution["sumo-retail"] = false;
        using var host = await BuildHostAsync(stub, authenticated: true);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/db/BravoDb");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Db_returns_404_when_connection_key_unknown()
    {
        var stub = new FakeEnvResolver();
        stub.AllowResolution["sumo-retail"] = true;
        // No entry for UnknownKey
        using var host = await BuildHostAsync(stub, authenticated: true);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/db/UnknownKey");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ServiceBus_returns_200_for_ConnectionString_mode()
    {
        var stub = new FakeEnvResolver();
        stub.AllowResolution["sumo-retail"] = true;
        stub.SbConnections[("DefaultBus", "sumo-retail")] = new ServiceBusConnectionConfig
        {
            AuthMode = ServiceBusAuthMode.ConnectionString,
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=Root;SharedAccessKey=abc",
        };

        using var host = await BuildHostAsync(stub, authenticated: true);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/servicebus/DefaultBus");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ServiceBusDto>();
        body!.AuthMode.Should().Be("ConnectionString");
        body.ConnectionString.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ServiceBus_returns_null_connection_string_for_AzureAd_mode()
    {
        var stub = new FakeEnvResolver();
        stub.AllowResolution["sumo-retail"] = true;
        stub.SbConnections[("DefaultBus", "sumo-retail")] = new ServiceBusConnectionConfig
        {
            AuthMode = ServiceBusAuthMode.AzureAd,
            FullyQualifiedNamespace = "test.servicebus.windows.net",
        };

        using var host = await BuildHostAsync(stub, authenticated: true);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/servicebus/DefaultBus");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ServiceBusDto>();
        body!.AuthMode.Should().Be("AzureAd");
        body.ConnectionString.Should().BeNull("AzureAd mode must not expose a connection string");
        body.FullyQualifiedNamespace.Should().Be("test.servicebus.windows.net");
    }

    [Fact]
    public async Task ServiceBus_returns_404_when_connection_key_unknown()
    {
        var stub = new FakeEnvResolver();
        stub.AllowResolution["sumo-retail"] = true;
        using var host = await BuildHostAsync(stub, authenticated: true);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/servicebus/UnknownBus");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Db_connection_string_value_appears_only_in_designated_field()
    {
        var stub = new FakeEnvResolver();
        stub.AllowResolution["sumo-retail"] = true;
        stub.DbConnections[("BravoDb", "sumo-retail")] = "Server=secret-server;Database=super-secret;";

        using var host = await BuildHostAsync(stub, authenticated: true);
        var resp = await host.GetTestClient().GetAsync("/api/environments/sumo-retail/connections/db/BravoDb");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseText = await resp.Content.ReadAsStringAsync();
        var count = CountOccurrences(responseText, "secret-server");
        count.Should().Be(1, "connection string value should appear exactly once in the response body");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        { count++; idx += pattern.Length; }
        return count;
    }

    private static async Task<IHost> BuildHostAsync(FakeEnvResolver resolver, bool authenticated = true)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddLogging();
                        services.AddSingleton<IEnvironmentResolver>(resolver);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        if (authenticated)
                        {
                            app.Use(async (httpCtx, next) =>
                            {
                                httpCtx.Items["User"] = new User
                                {
                                    Id = "test-user",
                                    Name = "Test User",
                                    ApiKey = "test-key",
                                    IsActive = true,
                                };
                                await next();
                            });
                        }
                        app.UseEndpoints(e =>
                        {
                            e.MapGroup("/api/environments/{envKey}/connections").MapAgentConfigEndpoints();
                        });
                    });
            });

        return await hostBuilder.StartAsync();
    }

    private record DbConnResponse(string ConnectionKey, string ConnectionString, string Source);
    private record ServiceBusDto(
        string ConnectionKey,
        string AuthMode,
        string? ConnectionString,
        string? FullyQualifiedNamespace,
        string? ManagedIdentityClientId);
}

/// <summary>Lightweight stub IEnvironmentResolver for AgentConfigEndpoints tests.</summary>
public sealed class FakeEnvResolver : IEnvironmentResolver
{
    public Dictionary<string, bool> AllowResolution { get; } = new();
    public Dictionary<(string key, string env), string?> DbConnections { get; } = new();
    public Dictionary<(string key, string env), ServiceBusConnectionConfig?> SbConnections { get; } = new();

    public EnvironmentConfig Resolve(string? key)
    {
        if (key is not null && AllowResolution.TryGetValue(key, out var allowed))
            return new EnvironmentConfig { AllowAgentConnectionResolution = allowed };
        return new EnvironmentConfig { AllowAgentConnectionResolution = true };
    }

    public string? ResolveDbConnectionString(string connectionKey, string? envKey)
        => DbConnections.TryGetValue((connectionKey, envKey ?? ""), out var v) ? v : null;

    public ServiceBusConnectionConfig? ResolveServiceBusConnection(string connectionKey, string? envKey)
        => SbConnections.TryGetValue((connectionKey, envKey ?? ""), out var v) ? v : null;

    public IReadOnlyList<string> ListDbConnectionKeys(string? envKey) => Array.Empty<string>();
    public IReadOnlyList<string> ListServiceBusConnectionKeys(string? envKey) => Array.Empty<string>();

    // Remaining interface members return safe defaults
    public string ResolveKey(string? requested) => requested ?? "default";
    public IReadOnlyCollection<string> ListKeys() => new[] { "default" };
    public string ResolveDisplayName(string? key) => key ?? "default";
    public string ResolveLegacyWebUiUrl(string? key) => "";
    public string ResolveLegacyWebUiUsername(string? key) => "";
    public string ResolveLegacyWebUiPassword(string? key) => "";
    public string? ResolveLegacyWebUiStorageStatePath(string? key) => null;
    public string ResolveBraveCloudUiUrl(string? key) => "";
    public string ResolveBraveCloudUiUsername(string? key) => "";
    public string ResolveBraveCloudUiPassword(string? key) => "";
    public string? ResolveBraveCloudUiStorageStatePath(string? key) => null;
    public string? ResolveBraveCloudUiTotpSecret(string? key) => null;
    public string ResolveWinFormsAppPath(string? key) => "";
    public string? ResolveWinFormsAppArgs(string? key) => null;
    public string ResolveBravoDbConnectionString(string? key) => "";
    public bool ResolveAllowDbDryRun(string? envKey) => true;
    public bool ResolveAllowEventAssertPeek(string? envKey) => true;
    public bool ResolveAllowApiDryRun(string? envKey) => true;
    public bool ResolveDataTeardownEnabled(string? key) => false;
    public bool ResolveRunDataPacksOnStartup(string? key) => false;
    public string ResolveApiStackBaseUrl(string? key, string stackKey) => "";
    public bool ResolveAuthHealthEnabled(string? key) => true;
}
