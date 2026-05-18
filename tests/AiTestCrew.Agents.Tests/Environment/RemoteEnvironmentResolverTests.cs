using System.Net;
using System.Text;
using System.Text.Json;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTestCrew.Agents.Tests.Environment;

/// <summary>
/// Unit tests for <see cref="RemoteEnvironmentResolver"/>.
/// Uses a fake HttpMessageHandler — no real HTTP calls.
/// The inner EnvironmentResolver is configured via a real TestEnvironmentConfig.
/// </summary>
public class RemoteEnvironmentResolverTests
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void ResolveDbConnectionString_returns_local_value_without_calling_server()
    {
        var cfg = ConfigWithDb("BravoDb", "Server=local;");
        var handler = new FakeSyncHttpHandler(HttpStatusCode.OK, new { connectionKey = "BravoDb", connectionString = "Server=server;", source = "TopLevel" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveDbConnectionString("BravoDb", null);

        result.Should().Be("Server=local;", "local resolver takes precedence");
        handler.CallCount.Should().Be(0, "no HTTP call when local config is present");
    }

    [Fact]
    public void ResolveDbConnectionString_calls_server_when_local_is_empty()
    {
        var cfg = new TestEnvironmentConfig();  // no local DB connections
        var handler = new FakeSyncHttpHandler(HttpStatusCode.OK, new { connectionKey = "BravoDb", connectionString = "Server=from-server;", source = "TopLevel" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveDbConnectionString("BravoDb", null);

        result.Should().Be("Server=from-server;");
        handler.CallCount.Should().Be(1, "should call server when local is empty");
    }

    [Fact]
    public void ResolveDbConnectionString_caches_server_result_on_second_call()
    {
        var cfg = new TestEnvironmentConfig();
        var handler = new FakeSyncHttpHandler(HttpStatusCode.OK, new { connectionKey = "BravoDb", connectionString = "Server=cached;", source = "TopLevel" });
        var sut = BuildSut(cfg, handler);

        var r1 = sut.ResolveDbConnectionString("BravoDb", null);
        var r2 = sut.ResolveDbConnectionString("BravoDb", null);

        r1.Should().Be("Server=cached;");
        r2.Should().Be("Server=cached;");
        handler.CallCount.Should().Be(1, "second call should use cache, not call server again");
    }

    [Fact]
    public void ResolveDbConnectionString_returns_null_on_server_404()
    {
        var cfg = new TestEnvironmentConfig();
        var handler = new FakeSyncHttpHandler(HttpStatusCode.NotFound, new { error = "not found" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveDbConnectionString("UnknownKey", null);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveDbConnectionString_returns_null_on_server_403()
    {
        var cfg = new TestEnvironmentConfig();
        var handler = new FakeSyncHttpHandler(HttpStatusCode.Forbidden, new { error = "forbidden" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveDbConnectionString("BravoDb", null);

        result.Should().BeNull("403 must return null, not throw");
    }

    [Fact]
    public void ResolveDbConnectionString_returns_null_on_network_error()
    {
        var cfg = new TestEnvironmentConfig();
        var handler = new ThrowingHttpHandler();
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveDbConnectionString("BravoDb", null);

        result.Should().BeNull("network error must return null, not throw");
    }

    [Fact]
    public void ResolveBravoDbConnectionString_routes_through_ResolveDbConnectionString()
    {
        var cfg = new TestEnvironmentConfig();
        var handler = new FakeSyncHttpHandler(HttpStatusCode.OK, new { connectionKey = "BravoDb", connectionString = "Server=bravo;", source = "Legacy" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveBravoDbConnectionString(null);

        result.Should().Be("Server=bravo;");
    }

    [Fact]
    public void ResolveServiceBusConnection_returns_local_config_without_calling_server()
    {
        var cfg = ConfigWithSb("DefaultBus", new ServiceBusConnectionConfig { AuthMode = ServiceBusAuthMode.ConnectionString, ConnectionString = "Endpoint=sb://local" });
        var handler = new FakeSyncHttpHandler(HttpStatusCode.OK, new { connectionKey = "DefaultBus", authMode = "ConnectionString", connectionString = "Endpoint=sb://server" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveServiceBusConnection("DefaultBus", null);

        result.Should().NotBeNull();
        result!.ConnectionString.Should().Be("Endpoint=sb://local", "local resolver takes precedence");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public void ResolveServiceBusConnection_calls_server_when_local_is_empty()
    {
        var cfg = new TestEnvironmentConfig();
        var handler = new FakeSyncHttpHandler(HttpStatusCode.OK, new { connectionKey = "DefaultBus", authMode = "ConnectionString", connectionString = "Endpoint=sb://from-server" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveServiceBusConnection("DefaultBus", null);

        result.Should().NotBeNull();
        result!.AuthMode.Should().Be(ServiceBusAuthMode.ConnectionString);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public void ResolveServiceBusConnection_returns_null_on_server_404()
    {
        var cfg = new TestEnvironmentConfig();
        var handler = new FakeSyncHttpHandler(HttpStatusCode.NotFound, new { error = "not found" });
        var sut = BuildSut(cfg, handler);

        var result = sut.ResolveServiceBusConnection("UnknownBus", null);

        result.Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RemoteEnvironmentResolver BuildSut(TestEnvironmentConfig cfg, HttpMessageHandler handler)
    {
        var inner  = new EnvironmentResolver(cfg);
        var client = new HttpClient(handler);
        return new RemoteEnvironmentResolver(
            inner, client,
            serverUrl: "http://fake-server",
            apiKey:    "test-key",
            logger:    NullLogger<RemoteEnvironmentResolver>.Instance);
    }

    private static TestEnvironmentConfig ConfigWithDb(string key, string connStr)
    {
        var cfg = new TestEnvironmentConfig();
        cfg.DbConnections[key] = connStr;
        return cfg;
    }

    private static TestEnvironmentConfig ConfigWithSb(string key, ServiceBusConnectionConfig sbCfg)
    {
        var cfg = new TestEnvironmentConfig();
        cfg.ServiceBusConnections[key] = sbCfg;
        return cfg;
    }

    /// <summary>
    /// Fake handler that returns a fixed response via the synchronous Send() path.
    /// </summary>
    private sealed class FakeSyncHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        public int CallCount { get; private set; }

        public FakeSyncHttpHandler(HttpStatusCode statusCode, object body)
        {
            _statusCode = statusCode;
            _body = JsonSerializer.Serialize(body, _json);
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    /// <summary>Simulates a network error (connection refused, DNS failure, etc.).</summary>
    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated network error");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated network error");
    }
}
