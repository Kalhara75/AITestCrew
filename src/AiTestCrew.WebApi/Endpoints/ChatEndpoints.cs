using System.Text.Json;
using AiTestCrew.Agents.Base;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.WebApi.Models.Chat;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/chat/message — resolve a natural-language turn into a structured reply + actions.
        // Persists the conversation when SQLite storage is active and the caller is authenticated;
        // otherwise behaves statelessly (legacy file-storage mode).
        group.MapPost("/message", async (ChatRequest request, IChatIntentService chat, HttpContext ctx, CancellationToken ct) =>
        {
            if (request is null)
                return Results.BadRequest(new { error = "request body is required" });
            if (string.IsNullOrWhiteSpace(request.Message)
                && (request.Messages is null || request.Messages.Count == 0))
                return Results.BadRequest(new { error = "message or messages is required" });

            var user = ctx.Items["User"] as User;
            try
            {
                var response = await chat.ProcessAsync(request, user?.Id, ct);
                return Results.Ok(response);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.NotFound(new { error = "Conversation not found." });
            }
        });

        // ─── Persisted conversation CRUD (per-user; requires X-Api-Key auth) ───

        // GET /api/chat/conversations — list the caller's conversations, newest first
        group.MapGet("/conversations", async (HttpContext ctx, IChatConversationRepository? repo, CancellationToken ct) =>
        {
            if (repo is null) return Results.Ok(Array.Empty<ConversationSummary>());
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();
            var list = await repo.ListByUserAsync(user.Id, ct);
            return Results.Ok(list.Select(c =>
                new ConversationSummary(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.MessageCount)));
        });

        // POST /api/chat/conversations — create an empty thread; returns the new id
        group.MapPost("/conversations", async (
            CreateConversationRequest? body,
            HttpContext ctx,
            IChatConversationRepository? repo,
            AiTestCrew.Core.Configuration.TestEnvironmentConfig cfg,
            CancellationToken ct) =>
        {
            if (repo is null) return Results.Problem("Conversation persistence is not configured.", statusCode: 501);
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();
            var title = string.IsNullOrWhiteSpace(body?.Title) ? "New chat" : body!.Title;
            var conv = await repo.CreateAsync(user.Id, title, cfg.Chat.MaxConversationsPerUser, ct);
            return Results.Created($"/api/chat/conversations/{conv.Id}",
                new ConversationSummary(conv.Id, conv.Title, conv.CreatedAt, conv.UpdatedAt, conv.MessageCount));
        });

        // GET /api/chat/conversations/{id} — full transcript for one conversation
        group.MapGet("/conversations/{id}", async (
            string id, HttpContext ctx, IChatConversationRepository? repo, CancellationToken ct) =>
        {
            if (repo is null) return Results.NotFound();
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();

            var conv = await repo.GetAsync(id, user.Id, ct);
            if (conv is null) return Results.NotFound(new { error = $"Conversation '{id}' not found" });

            var messages = await repo.GetMessagesAsync(id, user.Id, ct);
            var detail = new ConversationDetail(
                conv.Id, conv.Title, conv.CreatedAt, conv.UpdatedAt, conv.MessageCount,
                messages.Select(m => new PersistedChatMessage(
                    m.Id, m.Role, m.Content, ParseActions(m.ActionsJson), m.CreatedAt)).ToList());
            return Results.Ok(detail);
        });

        // DELETE /api/chat/conversations/{id}
        group.MapDelete("/conversations/{id}", async (
            string id, HttpContext ctx, IChatConversationRepository? repo, CancellationToken ct) =>
        {
            if (repo is null) return Results.NotFound();
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();

            var existing = await repo.GetAsync(id, user.Id, ct);
            if (existing is null) return Results.NotFound(new { error = $"Conversation '{id}' not found" });

            await repo.DeleteAsync(id, user.Id, ct);
            return Results.NoContent();
        });

        // PATCH /api/chat/conversations/{id} — rename
        group.MapPatch("/conversations/{id}", async (
            string id, RenameConversationRequest body, HttpContext ctx,
            IChatConversationRepository? repo, CancellationToken ct) =>
        {
            if (repo is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body?.Title))
                return Results.BadRequest(new { error = "title is required" });
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();

            var existing = await repo.GetAsync(id, user.Id, ct);
            if (existing is null) return Results.NotFound(new { error = $"Conversation '{id}' not found" });

            await repo.RenameAsync(id, user.Id, body.Title, ct);
            return Results.Ok(new ConversationSummary(
                existing.Id, body.Title, existing.CreatedAt, DateTime.UtcNow, existing.MessageCount));
        });

        return group;
    }

    private static List<ChatAction>? ParseActions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<ChatAction>>(json, LlmJsonHelper.JsonOpts); }
        catch { return null; }
    }
}
