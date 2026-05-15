using System.Net;
using System.Net.Http.Json;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;
using AiTestCrew.WebApi.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

/// <summary>
/// Integration tests for <c>POST /api/llm/chat</c>.
/// Spins up a minimal in-memory host with a fake IChatCompletionService —
/// avoids the full WebApi pipeline (LLM keys, persistence, etc.).
/// </summary>
public class LlmEndpointsTests
{
    [Fact]
    public async Task Post_returns_200_with_content_on_happy_path()
    {
        using var host = await BuildHostAsync(authenticated: true);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/llm/chat", new
        {
            messages = new[]
            {
                new { role = "user", content = "Hello" },
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LlmChatResponse>();
        body.Should().NotBeNull();
        body!.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Post_returns_401_when_unauthenticated()
    {
        using var host = await BuildHostAsync(authenticated: false);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/llm/chat", new
        {
            messages = new[] { new { role = "user", content = "Hello" } },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_returns_400_when_messages_empty()
    {
        using var host = await BuildHostAsync(authenticated: true);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/llm/chat", new
        {
            messages = Array.Empty<object>(),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<IHost> BuildHostAsync(bool authenticated = true)
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
                        services.AddSingleton<TestEnvironmentConfig>(new TestEnvironmentConfig());
                        // Fake IChatCompletionService that returns a canned response
                        services.AddSingleton<IChatCompletionService>(new FakeChatCompletionService());
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
                            e.MapGroup("/api/llm").MapLlmEndpoints();
                        });
                    });
            });

        return await hostBuilder.StartAsync();
    }

    private record LlmChatResponse(string Content, string Model);

    // ── Minimal fake IChatCompletionService ──────────────────────────────────

    private sealed class FakeChatCompletionService : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes { get; } =
            new Dictionary<string, object?> { ["model"] = "fake-model" };

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ChatMessageContent> result =
                [new ChatMessageContent(AuthorRole.Assistant, "test response")];
            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "test");
        }
    }
}
