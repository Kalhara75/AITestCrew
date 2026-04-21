using AiTestCrew.WebApi.Models.Chat;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/chat/message — resolve a natural-language turn into a structured reply + actions
        group.MapPost("/message", async (ChatRequest request, IChatIntentService chat, CancellationToken ct) =>
        {
            if (request?.Messages is null || request.Messages.Count == 0)
                return Results.BadRequest(new { error = "messages is required" });

            var response = await chat.ProcessAsync(request, ct);
            return Results.Ok(response);
        });

        return group;
    }
}
