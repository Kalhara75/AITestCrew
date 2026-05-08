using System.Globalization;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Core.Utilities;
using AiTestCrew.WebApi.Services;
using Microsoft.Data.SqlClient;

namespace AiTestCrew.WebApi.Endpoints;

/// <summary>
/// REQ-002 endpoints powering the editor's "Try query" preview and the
/// connection-key dropdown:
/// <list type="bullet">
///   <item><c>POST /api/db-check/dry-run</c> — runs a SELECT against an
///     environment's configured DB and returns columns + first 5 rows.</item>
///   <item><c>GET /api/db-check/connections</c> — lists the logical
///     connection keys configured for an env.</item>
/// </list>
/// </summary>
public static class DbCheckEndpoints
{
    private const int MaxResponseRows = 5;
    private const int CellTruncate = 500;
    private const int DryRunCommandTimeoutSeconds = 10;

    public static RouteGroupBuilder MapDbCheckEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/dry-run", async (
            DryRunRequest? body,
            HttpContext ctx,
            IEnvironmentResolver envResolver,
            DbDryRunRateLimiter rateLimiter,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "request body is required" });
            if (string.IsNullOrWhiteSpace(body.Sql))
                return Results.BadRequest(new { error = "sql is required" });

            var connectionKey = string.IsNullOrWhiteSpace(body.ConnectionKey) ? "BravoDb" : body.ConnectionKey!;

            // Rate limit per user (or per IP if auth is disabled).
            var rateKey = (ctx.Items["User"] as User)?.Id
                ?? ctx.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous";
            if (!rateLimiter.TryAcquire(rateKey))
            {
                return Results.Json(
                    new { error = "Too many DB dry-run requests; try again in a minute." },
                    statusCode: 429);
            }

            // Per-env opt-out.
            if (!envResolver.ResolveAllowDbDryRun(body.EnvKey))
            {
                return Results.Json(
                    new { error = $"DB dry-run is disabled for env '{body.EnvKey ?? "default"}'." },
                    statusCode: 403);
            }

            // Substitute caller-supplied parameters into the SQL preview.
            var paramDict = body.Parameters is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(body.Parameters, StringComparer.OrdinalIgnoreCase);
            var substitutedSql = TokenSubstituter.Substitute(body.Sql, paramDict, throwOnMissing: false) ?? "";

            // Guardrails before we even resolve a connection — stops obvious abuse.
            var (ok, reason) = DbCheckSqlGuardrails.Validate(substitutedSql);
            if (!ok)
                return Results.BadRequest(new { error = reason });

            var connectionString = envResolver.ResolveDbConnectionString(connectionKey, body.EnvKey);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return Results.NotFound(new
                {
                    error = $"DB connection key '{connectionKey}' is not configured for env '{body.EnvKey ?? "default"}'.",
                });
            }

            var logger = loggerFactory.CreateLogger("DbCheckEndpoints");
            try
            {
                await using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct);

                await using var cmd = new SqlCommand(substitutedSql, conn)
                {
                    CommandTimeout = DryRunCommandTimeoutSeconds,
                };

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                var columns = new List<DryRunColumn>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(new DryRunColumn(
                        reader.GetName(i),
                        reader.GetDataTypeName(i)));
                }

                var rows = new List<Dictionary<string, string?>>();
                var totalRowCount = 0;
                while (await reader.ReadAsync(ct))
                {
                    totalRowCount++;
                    if (rows.Count >= MaxResponseRows) continue;

                    var row = new Dictionary<string, string?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (reader.IsDBNull(i))
                        {
                            row[reader.GetName(i)] = null;
                            continue;
                        }
                        var raw = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                        row[reader.GetName(i)] = raw.Length <= CellTruncate ? raw : raw[..CellTruncate] + "…";
                    }
                    rows.Add(row);
                }

                return Results.Ok(new DryRunResponse(columns, rows, totalRowCount));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "DB dry-run failed for connection '{Key}' env '{Env}'",
                    connectionKey, body.EnvKey ?? "default");
                return Results.Problem(
                    detail: ex.Message,
                    title: "DB dry-run failed",
                    statusCode: 500);
            }
        });

        group.MapGet("/connections", (
            string? envKey,
            IEnvironmentResolver envResolver) =>
        {
            var keys = envResolver.ListDbConnectionKeys(envKey);
            return Results.Ok(new { keys });
        });

        return group;
    }
}

/// <summary>Request body for <c>POST /api/db-check/dry-run</c>.</summary>
public class DryRunRequest
{
    public string? EnvKey { get; set; }
    public string? ConnectionKey { get; set; }
    public string Sql { get; set; } = "";
    public Dictionary<string, string>? Parameters { get; set; }
}

public record DryRunColumn(string Name, string SqlType);
public record DryRunResponse(
    IReadOnlyList<DryRunColumn> Columns,
    IReadOnlyList<Dictionary<string, string?>> Rows,
    int TotalRowCount);
