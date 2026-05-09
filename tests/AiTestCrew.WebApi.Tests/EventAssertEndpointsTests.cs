using System.Net;
using System.Net.Http.Json;
using System.Text;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
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
/// Integration tests for <c>POST /api/event-assert/peek</c> +
/// <c>GET /api/event-assert/connections</c>. Spins up a minimal in-memory
/// host with a fake <see cref="IServiceBusReceiverFactory"/> so we can
/// exercise the gating + projection logic without an Azure dependency.
/// </summary>
public class EventAssertEndpointsTests
{
    private const string ConnectionKey = "DefaultBus";

    [Fact]
    public async Task Connections_lists_configured_keys()
    {
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                [ConnectionKey] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "Endpoint=sb://x;SharedAccessKey=y",
                },
            },
        };
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.GetAsync("/api/event-assert/connections?envKey=default");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<KeysResponse>();
        body!.Keys.Should().Contain(ConnectionKey);
    }

    [Fact]
    public async Task Peek_returns_401_when_unauthenticated()
    {
        var cfg = ConfigWithDefaultBus();
        using var host = await BuildHostAsync(cfg, authenticated: false);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/event-assert/peek", new
        {
            envKey = (string?)null,
            connectionKey = ConnectionKey,
            entity = new { type = "Queue", name = "q" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Peek_returns_404_on_unknown_connection()
    {
        var cfg = new TestEnvironmentConfig();  // no ServiceBusConnections at all
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/event-assert/peek", new
        {
            envKey = (string?)null,
            connectionKey = "Missing",
            entity = new { type = "Queue", name = "q" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Peek_returns_403_when_env_opts_out()
    {
        var cfg = ConfigWithDefaultBus();
        cfg.Environments["prod"] = new EnvironmentConfig { AllowEventAssertPeek = false };
        cfg.Environments["prod"].ServiceBusConnections[ConnectionKey] =
            new ServiceBusConnectionConfig
            {
                AuthMode = ServiceBusAuthMode.ConnectionString,
                ConnectionString = "Endpoint=sb://prod;SharedAccessKey=k",
            };
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/event-assert/peek", new
        {
            envKey = "prod",
            connectionKey = ConnectionKey,
            entity = new { type = "Queue", name = "q" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Peek_rejects_topic_without_subscription()
    {
        var cfg = ConfigWithDefaultBus();
        using var host = await BuildHostAsync(cfg);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/event-assert/peek", new
        {
            envKey = (string?)null,
            connectionKey = ConnectionKey,
            entity = new { type = "Topic", name = "t" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Peek_rate_limits_after_threshold_per_user()
    {
        var cfg = ConfigWithDefaultBus();
        using var host = await BuildHostAsync(
            cfg,
            limiter: new EventAssertPeekRateLimiter(maxPerWindow: 2, window: TimeSpan.FromMinutes(1)));
        var client = host.GetTestClient();

        async Task<HttpStatusCode> Hit() => (await client.PostAsJsonAsync("/api/event-assert/peek", new
        {
            envKey = (string?)null,
            connectionKey = ConnectionKey,
            entity = new { type = "Queue", name = "q" },
        })).StatusCode;

        var first = await Hit();
        var second = await Hit();
        var third = await Hit();

        first.Should().NotBe(HttpStatusCode.TooManyRequests);
        second.Should().NotBe(HttpStatusCode.TooManyRequests);
        third.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Peek_returns_200_with_truncated_body_preview()
    {
        var cfg = ConfigWithDefaultBus();
        var fake = new InlineFakeReceiverFactory();
        fake.Pending.Enqueue(new ReceivedMessageView
        {
            MessageId = "msg-1",
            CorrelationId = "corr",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"OrderId\":\"42\"}"),
            EnqueuedTimeUtc = DateTimeOffset.UtcNow,
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["EventType"] = "Created",
            },
            DeliveryCount = 1,
        });

        using var host = await BuildHostAsync(cfg, factory: fake);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/event-assert/peek", new
        {
            envKey = (string?)null,
            connectionKey = ConnectionKey,
            entity = new { type = "Queue", name = "q" },
            max = 5,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PeekResponseDto>();
        body!.TotalPeeked.Should().Be(1);
        body.Messages.Should().HaveCount(1);
        var msg = body.Messages[0];
        msg.MessageId.Should().Be("msg-1");
        msg.CorrelationId.Should().Be("corr");
        msg.Body.Format.Should().Be("Json");
        msg.Body.Preview.Should().Contain("\"OrderId\":\"42\"");
        msg.ApplicationProperties.Should().ContainKey("EventType")
            .WhoseValue.Should().Be("Created");
    }

    [Fact]
    public async Task Peek_correlation_filter_drops_non_matching_messages()
    {
        var cfg = ConfigWithDefaultBus();
        var fake = new InlineFakeReceiverFactory();
        fake.Pending.Enqueue(new ReceivedMessageView { MessageId = "1", CorrelationId = "other" });
        fake.Pending.Enqueue(new ReceivedMessageView { MessageId = "2", CorrelationId = "mine" });

        using var host = await BuildHostAsync(cfg, factory: fake);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/event-assert/peek", new
        {
            envKey = (string?)null,
            connectionKey = ConnectionKey,
            entity = new { type = "Queue", name = "q" },
            correlationFilter = "mine",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PeekResponseDto>();
        body!.Messages.Should().ContainSingle().Which.MessageId.Should().Be("2");
        // totalPeeked reflects what came off the bus, not what the filter kept.
        body.TotalPeeked.Should().Be(2);
    }

    // ── Test host wiring ───────────────────────────────────────────────

    private static TestEnvironmentConfig ConfigWithDefaultBus() =>
        new()
        {
            ServiceBusConnections =
            {
                [ConnectionKey] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "Endpoint=sb://x;SharedAccessKey=y",
                },
            },
        };

    private static async Task<IHost> BuildHostAsync(
        TestEnvironmentConfig cfg,
        EventAssertPeekRateLimiter? limiter = null,
        IServiceBusReceiverFactory? factory = null,
        bool authenticated = true)
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
                        services.AddSingleton(limiter ?? new EventAssertPeekRateLimiter());
                        services.AddSingleton<IServiceBusReceiverFactory>(
                            factory ?? new InlineFakeReceiverFactory());
                        services.AddLogging();
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
                            e.MapGroup("/api/event-assert").MapEventAssertEndpoints();
                        });
                    });
            });
        return await hostBuilder.StartAsync();
    }

    // ── Minimal in-test fake (mirrors AiTestCrew.Agents.Tests' fake) ───

    private sealed class InlineFakeReceiverFactory : IServiceBusReceiverFactory
    {
        public Queue<ReceivedMessageView> Pending { get; } = new();

        public Task<IServiceBusReceiverHandle> OpenAsync(
            ServiceBusConnectionConfig connection,
            ServiceBusEntity entity,
            ReceiveMode mode,
            string? sessionId,
            CancellationToken ct)
            => Task.FromResult<IServiceBusReceiverHandle>(new Handle(this));

        private sealed class Handle : IServiceBusReceiverHandle
        {
            private readonly InlineFakeReceiverFactory _parent;
            public Handle(InlineFakeReceiverFactory parent) { _parent = parent; }

            public Task<IReadOnlyList<ReceivedMessageView>> ReceiveBatchAsync(
                int maxMessages, TimeSpan perCallTimeout, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<ReceivedMessageView>>(Array.Empty<ReceivedMessageView>());

            public Task<IReadOnlyList<ReceivedMessageView>> PeekBatchAsync(
                int maxMessages, CancellationToken ct)
            {
                var batch = new List<ReceivedMessageView>();
                while (batch.Count < maxMessages && _parent.Pending.TryDequeue(out var m))
                    batch.Add(m);
                return Task.FromResult<IReadOnlyList<ReceivedMessageView>>(batch);
            }

            public Task CompleteAsync(ReceivedMessageView message, CancellationToken ct) => Task.CompletedTask;
            public Task AbandonAsync(ReceivedMessageView message, CancellationToken ct) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // ── DTOs for response deserialisation ───────────────────────────────

    private record KeysResponse(IReadOnlyList<string> Keys);
    private record PeekResponseDto(IReadOnlyList<PeekMessageDto> Messages, int TotalPeeked);
    private record PeekMessageDto(
        string? MessageId,
        string? CorrelationId,
        string? Subject,
        string? ContentType,
        DateTimeOffset EnqueuedTimeUtc,
        int DeliveryCount,
        IReadOnlyDictionary<string, string> ApplicationProperties,
        PeekBodyDto Body);
    private record PeekBodyDto(string Format, string Preview, int Length);
}
