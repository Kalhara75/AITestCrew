using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models.Chat;
using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IChatConversationRepository"/>.
/// Every operation is filtered by <c>user_id</c> so a stolen conversation id
/// alone cannot read another user's thread.
/// </summary>
public sealed class SqliteChatConversationRepository : IChatConversationRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteChatConversationRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<ChatConversation> CreateAsync(string userId, string title, int maxConversationsPerUser, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var conv = new ChatConversation
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title,
            CreatedAt = now,
            UpdatedAt = now,
            MessageCount = 0
        };

        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Enforce per-user retention cap: prune oldest first, then insert.
        if (maxConversationsPerUser > 0)
        {
            using var prune = conn.CreateCommand();
            prune.Transaction = tx;
            prune.CommandText = """
                DELETE FROM chat_messages
                WHERE conversation_id IN (
                    SELECT id FROM chat_conversations
                    WHERE user_id = $userId
                    ORDER BY updated_at DESC
                    LIMIT -1 OFFSET $keep
                );
                DELETE FROM chat_conversations
                WHERE user_id = $userId
                  AND id IN (
                    SELECT id FROM chat_conversations
                    WHERE user_id = $userId
                    ORDER BY updated_at DESC
                    LIMIT -1 OFFSET $keep
                );
                """;
            prune.Parameters.AddWithValue("$userId", userId);
            // After insert there will be N+1 rows; keep N-1 existing so the new one fits inside the cap.
            prune.Parameters.AddWithValue("$keep", Math.Max(0, maxConversationsPerUser - 1));
            await prune.ExecuteNonQueryAsync(ct);
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO chat_conversations (id, user_id, title, created_at, updated_at, message_count)
            VALUES ($id, $userId, $title, $createdAt, $updatedAt, 0)
            """;
        cmd.Parameters.AddWithValue("$id", conv.Id);
        cmd.Parameters.AddWithValue("$userId", conv.UserId);
        cmd.Parameters.AddWithValue("$title", conv.Title);
        cmd.Parameters.AddWithValue("$createdAt", conv.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", conv.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        tx.Commit();
        return conv;
    }

    public async Task<IReadOnlyList<ChatConversation>> ListByUserAsync(string userId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE user_id = $userId ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$userId", userId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ChatConversation>();
        while (await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    public async Task<ChatConversation?> GetAsync(string conversationId, string userId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE id = $id AND user_id = $userId";
        cmd.Parameters.AddWithValue("$id", conversationId);
        cmd.Parameters.AddWithValue("$userId", userId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<ChatMessageRecord>> GetMessagesAsync(string conversationId, string userId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // The JOIN enforces user ownership: a message is only returned if its
        // conversation belongs to the caller. No row-level fakery possible.
        cmd.CommandText = """
            SELECT m.id, m.conversation_id, m.role, m.content, m.actions_json, m.created_at
            FROM chat_messages m
            INNER JOIN chat_conversations c ON c.id = m.conversation_id
            WHERE m.conversation_id = $id AND c.user_id = $userId
            ORDER BY m.created_at ASC, m.id ASC
            """;
        cmd.Parameters.AddWithValue("$id", conversationId);
        cmd.Parameters.AddWithValue("$userId", userId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ChatMessageRecord>();
        while (await reader.ReadAsync(ct))
            result.Add(ReadMessage(reader));
        return result;
    }

    public async Task AppendMessageAsync(string conversationId, string userId, ChatMessageRecord message, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Verify ownership inside the transaction; bumping updated_at for a
        // foreign user must be impossible.
        using (var ownership = conn.CreateCommand())
        {
            ownership.Transaction = tx;
            ownership.CommandText = "SELECT 1 FROM chat_conversations WHERE id = $id AND user_id = $userId";
            ownership.Parameters.AddWithValue("$id", conversationId);
            ownership.Parameters.AddWithValue("$userId", userId);
            var owns = await ownership.ExecuteScalarAsync(ct);
            if (owns is null)
                throw new UnauthorizedAccessException($"Conversation '{conversationId}' not found for user.");
        }

        if (string.IsNullOrEmpty(message.Id))
            message.Id = Guid.NewGuid().ToString("N");
        if (message.CreatedAt == default)
            message.CreatedAt = DateTime.UtcNow;
        message.ConversationId = conversationId;

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO chat_messages (id, conversation_id, role, content, actions_json, created_at)
                VALUES ($id, $convId, $role, $content, $actions, $createdAt)
                """;
            insert.Parameters.AddWithValue("$id", message.Id);
            insert.Parameters.AddWithValue("$convId", conversationId);
            insert.Parameters.AddWithValue("$role", message.Role);
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$actions", (object?)message.ActionsJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        using (var bump = conn.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = """
                UPDATE chat_conversations
                SET updated_at = $now,
                    message_count = message_count + 1
                WHERE id = $id AND user_id = $userId
                """;
            bump.Parameters.AddWithValue("$id", conversationId);
            bump.Parameters.AddWithValue("$userId", userId);
            bump.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await bump.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    public async Task DeleteAsync(string conversationId, string userId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        using (var delMsgs = conn.CreateCommand())
        {
            delMsgs.Transaction = tx;
            delMsgs.CommandText = """
                DELETE FROM chat_messages
                WHERE conversation_id IN (
                    SELECT id FROM chat_conversations WHERE id = $id AND user_id = $userId
                )
                """;
            delMsgs.Parameters.AddWithValue("$id", conversationId);
            delMsgs.Parameters.AddWithValue("$userId", userId);
            await delMsgs.ExecuteNonQueryAsync(ct);
        }

        using (var delConv = conn.CreateCommand())
        {
            delConv.Transaction = tx;
            delConv.CommandText = "DELETE FROM chat_conversations WHERE id = $id AND user_id = $userId";
            delConv.Parameters.AddWithValue("$id", conversationId);
            delConv.Parameters.AddWithValue("$userId", userId);
            await delConv.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    public async Task RenameAsync(string conversationId, string userId, string title, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE chat_conversations
            SET title = $title, updated_at = $now
            WHERE id = $id AND user_id = $userId
            """;
        cmd.Parameters.AddWithValue("$id", conversationId);
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? "Untitled" : title);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string SelectSql =
        "SELECT id, user_id, title, created_at, updated_at, message_count FROM chat_conversations";

    private static ChatConversation Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        UserId = r.GetString(1),
        Title = r.GetString(2),
        CreatedAt = DateTime.Parse(r.GetString(3)).ToUniversalTime(),
        UpdatedAt = DateTime.Parse(r.GetString(4)).ToUniversalTime(),
        MessageCount = r.GetInt32(5)
    };

    private static ChatMessageRecord ReadMessage(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        ConversationId = r.GetString(1),
        Role = r.GetString(2),
        Content = r.GetString(3),
        ActionsJson = r.IsDBNull(4) ? null : r.GetString(4),
        CreatedAt = DateTime.Parse(r.GetString(5)).ToUniversalTime()
    };
}
