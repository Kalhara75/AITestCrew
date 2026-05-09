using System.Text;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.EventAssertAgent.Body;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

/// <summary>
/// REQ-004 endpoints powering the editor's "Peek messages" preview and the
/// connection-key dropdown:
/// <list type="bullet">
///   <item><c>POST /api/event-assert/peek</c> — returns up to N messages from
///     a queue / topic+subscription WITHOUT consuming them. Critical: a UI
///     button must never accidentally drain a real test run.</item>
///   <item><c>GET /api/event-assert/connections</c> — lists the logical
///     Service Bus connection keys configured for an env.</item>
/// </list>
///
/// Auth + rate-limit + per-env opt-in mirrors REQ-002's
/// <see cref="DbCheckEndpoints"/> pattern. The TODO consolidation noted in the
/// plan (one shared <c>PerUserTokenBucket</c>) is deferred — Phase 6 ships
/// duplicates so the slice doesn't drag REQ-002 in.
/// </summary>
public static class EventAssertEndpoints
{
    private const int DefaultMaxMessages = 10;
    private const int MaxMaxMessages = 50;
    private const int BodyPreviewMaxBytes = 2048;

    public static RouteGroupBuilder MapEventAssertEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/peek", async (
            PeekRequest? body,
            HttpContext ctx,
            IEnvironmentResolver envResolver,
            IServiceBusReceiverFactory receiverFactory,
            EventAssertPeekRateLimiter rateLimiter,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            // Auth first — same reason as DbCheckEndpoints: anonymous callers
            // mustn't be able to starve another user's bucket via a shared key.
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();

            if (body is null)
                return Results.BadRequest(new { error = "request body is required" });
            if (string.IsNullOrWhiteSpace(body.ConnectionKey))
                return Results.BadRequest(new { error = "connectionKey is required" });
            if (body.Entity is null || string.IsNullOrWhiteSpace(body.Entity.Name))
                return Results.BadRequest(new { error = "entity.name is required" });
            if (body.Entity.Type == ServiceBusEntityType.Topic
                && string.IsNullOrWhiteSpace(body.Entity.SubscriptionName))
            {
                return Results.BadRequest(new
                {
                    error = $"entity.subscriptionName is required when entity.type is Topic ('{body.Entity.Name}')",
                });
            }

            // Rate limit per authenticated user.
            if (!rateLimiter.TryAcquire(user.Id))
            {
                return Results.Json(
                    new { error = "Too many event-assert peek requests; try again in a minute." },
                    statusCode: 429);
            }

            // Per-env opt-out.
            if (!envResolver.ResolveAllowEventAssertPeek(body.EnvKey))
            {
                return Results.Json(
                    new { error = $"Event-assert peek is disabled for env '{body.EnvKey ?? "default"}'." },
                    statusCode: 403);
            }

            var connection = envResolver.ResolveServiceBusConnection(body.ConnectionKey, body.EnvKey);
            if (connection is null)
            {
                return Results.NotFound(new
                {
                    error = $"Service Bus connection key '{body.ConnectionKey}' is not configured for env '{body.EnvKey ?? "default"}'.",
                });
            }

            var max = body.Max is int m && m > 0
                ? Math.Min(m, MaxMaxMessages)
                : DefaultMaxMessages;

            var logger = loggerFactory.CreateLogger("EventAssertEndpoints");
            try
            {
                // PeekLock here is irrelevant — we use PeekBatchAsync, which
                // never locks or consumes. Pass PeekLock to keep the SDK happy.
                await using var receiver = await receiverFactory.OpenAsync(
                    connection, body.Entity, ReceiveMode.PeekLock, sessionId: null, ct);

                var raw = await receiver.PeekBatchAsync(max, ct);

                var messages = new List<PeekMessage>(raw.Count);
                foreach (var msg in raw)
                {
                    // Decompress framework-applied compression (Rebus / NServiceBus
                    // gzip via rbs2-content-encoding) so the editor's preview and
                    // the chat's peek card both show readable JSON / XML.
                    var dec = BodyDecompressor.MaybeDecompress(msg.Body, msg.ApplicationProperties);
                    var fmt = BodyFormatDetector.Resolve(BodyFormat.Auto, msg.ContentType, dec.Body);
                    messages.Add(new PeekMessage(
                        MessageId: msg.MessageId,
                        CorrelationId: msg.CorrelationId,
                        Subject: msg.Subject,
                        ContentType: msg.ContentType,
                        EnqueuedTimeUtc: msg.EnqueuedTimeUtc,
                        DeliveryCount: msg.DeliveryCount,
                        ApplicationProperties: msg.ApplicationProperties.ToDictionary(
                            kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
                        Body: new PeekBody(fmt.ToString(), TruncatePreview(dec.Body, fmt), dec.Body.Length)));
                }

                // Optional client-side correlation filter — applied AFTER the peek
                // so the response shape is consistent. Useful when a UI is testing
                // their criterion phrasing against a busy shared sub.
                if (!string.IsNullOrEmpty(body.CorrelationFilter))
                {
                    messages = messages
                        .Where(m => string.Equals(m.CorrelationId, body.CorrelationFilter, StringComparison.Ordinal))
                        .ToList();
                }

                return Results.Ok(new PeekResponse(messages, raw.Count));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Service Bus peek failed for connection '{Key}' env '{Env}' entity '{Entity}'",
                    body.ConnectionKey, body.EnvKey ?? "default", body.Entity.Name);
                return Results.Problem(
                    detail: ex.Message,
                    title: "Event-assert peek failed",
                    statusCode: 500);
            }
        });

        group.MapGet("/connections", (
            string? envKey,
            HttpContext ctx,
            IEnvironmentResolver envResolver) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();

            var keys = envResolver.ListServiceBusConnectionKeys(envKey);
            return Results.Ok(new { keys });
        });

        return group;
    }

    private static string TruncatePreview(byte[] body, BodyFormat fmt)
    {
        if (body.Length == 0) return "";
        if (fmt == BodyFormat.Binary) return $"<{body.Length} bytes — binary>";
        try
        {
            var text = Encoding.UTF8.GetString(body);
            if (text.Length > BodyPreviewMaxBytes)
                return text[..BodyPreviewMaxBytes] + "…";
            return text;
        }
        catch
        {
            return $"<{body.Length} bytes — non-UTF8>";
        }
    }
}

// ── Wire types ─────────────────────────────────────────────────────────

/// <summary>Request body for <c>POST /api/event-assert/peek</c>.</summary>
public class PeekRequest
{
    public string? EnvKey { get; set; }
    public string? ConnectionKey { get; set; }
    public ServiceBusEntity? Entity { get; set; }
    public int? Max { get; set; }
    public string? CorrelationFilter { get; set; }
}

public record PeekMessage(
    string? MessageId,
    string? CorrelationId,
    string? Subject,
    string? ContentType,
    DateTimeOffset EnqueuedTimeUtc,
    int DeliveryCount,
    IReadOnlyDictionary<string, string> ApplicationProperties,
    PeekBody Body);

public record PeekBody(string Format, string Preview, int Length);

public record PeekResponse(IReadOnlyList<PeekMessage> Messages, int TotalPeeked);
