using System.Net;
using System.Text.Json;
using AiTestCrew.Agents.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AiTestCrew.Agents.Tests.Llm;

/// <summary>
/// Unit tests for <see cref="RemoteChatCompletionService"/>.
/// Uses a fake <see cref="HttpMessageHandler"/> — no real HTTP calls.
/// </summary>
public class RemoteChatCompletionServiceTests
{
    [Fact]
    public async Task GetChatMessageContentsAsync_returns_content_on_happy_path()
    {
        var fakeResponse = new
        {
            content = "Hello from the server",
            model   = "claude-sonnet-4-6",
        };
        var handler  = new FakeHttpHandler(HttpStatusCode.OK, fakeResponse);
        var service  = BuildService(handler);
        var history  = new ChatHistory();
        history.AddUserMessage("Hi");

        var results = await service.GetChatMessageContentsAsync(history);

        results.Should().HaveCount(1);
        results[0].Content.Should().Be("Hello from the server");
    }

    [Fact]
    public async Task GetChatMessageContentsAsync_throws_on_server_error()
    {
        var errorBody = new { error = "LLM failed", providerError = "provider raw error" };
        var handler  = new FakeHttpHandler(HttpStatusCode.BadGateway, errorBody);
        var service  = BuildService(handler);
        var history  = new ChatHistory();
        history.AddUserMessage("Hi");

        var act = async () => await service.GetChatMessageContentsAsync(history);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*502*");
    }

    [Fact]
    public void GetStreamingChatMessageContentsAsync_throws_not_supported()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, new { content = "" });
        var service = BuildService(handler);
        var history = new ChatHistory();
        history.AddUserMessage("Hi");

        // Streaming is synchronously unsupported — throws immediately on call, not on enumeration.
        var act = () => service.GetStreamingChatMessageContentsAsync(history);
        act.Should().Throw<NotSupportedException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RemoteChatCompletionService BuildService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new RemoteChatCompletionService(
            client,
            serverUrl:     "http://fake-server",
            apiKey:        "test-key",
            defaultModel:  "claude-sonnet-4-6",
            logger:        NullLogger<RemoteChatCompletionService>.Instance);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public FakeHttpHandler(HttpStatusCode statusCode, object body)
        {
            _statusCode = statusCode;
            _body = JsonSerializer.Serialize(body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
