using System.Diagnostics;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

public static class AgentConfigEndpoints
{
    public static RouteGroupBuilder MapAgentConfigEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/environments/{envKey}/connections/db/{connectionKey}
        // Resolves a SQL Server connection string for a remote agent.
        group.MapGet("/db/{connectionKey}", (
            string envKey,
            string connectionKey,
            IEnvironmentResolver envResolver,
            HttpContext ctx,
            ILoggerFactory loggerFactory) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null)
                return Results.Unauthorized();

            var env = envResolver.Resolve(envKey);
            if (!env.AllowAgentConnectionResolution)
                return Results.Problem(
                    title: "Connection resolution disabled",
                    detail: $"env '{envKey}' has agent connection resolution disabled - run from server or set AllowAgentConnectionResolution=true",
                    statusCode: 403);

            var sw = Stopwatch.StartNew();
            var connStr = envResolver.ResolveDbConnectionString(connectionKey, envKey);
            sw.Stop();

            var source = DetermineDbSource(envResolver, connectionKey, envKey);
            var logger = loggerFactory.CreateLogger("AgentConfig");

            logger.LogInformation(
                "AgentConfig/db: user={UserId} agent={AgentId} env={EnvKey} key={ConnectionKey} source={Source} found={Found} latencyMs={LatencyMs}",
                user.Id,
                ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "unknown",
                envKey, connectionKey, source, connStr is not null, sw.ElapsedMilliseconds);
            // IMPORTANT: connection string value is never logged

            if (connStr is null)
                return Results.NotFound(new { error = $"No DB connection configured for key '{connectionKey}' in environment '{envKey}'" });

            return Results.Ok(new DbConnectionResponse(connectionKey, connStr, source));
        });

        // GET /api/environments/{envKey}/connections/servicebus/{connectionKey}
        group.MapGet("/servicebus/{connectionKey}", (
            string envKey,
            string connectionKey,
            IEnvironmentResolver envResolver,
            HttpContext ctx,
            ILoggerFactory loggerFactory) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null)
                return Results.Unauthorized();

            var env = envResolver.Resolve(envKey);
            if (!env.AllowAgentConnectionResolution)
                return Results.Problem(
                    title: "Connection resolution disabled",
                    detail: $"env '{envKey}' has agent connection resolution disabled - run from server or set AllowAgentConnectionResolution=true",
                    statusCode: 403);

            var sw = Stopwatch.StartNew();
            var sbConfig = envResolver.ResolveServiceBusConnection(connectionKey, envKey);
            sw.Stop();

            var logger = loggerFactory.CreateLogger("AgentConfig");
            logger.LogInformation(
                "AgentConfig/servicebus: user={UserId} agent={AgentId} env={EnvKey} key={ConnectionKey} found={Found} authMode={AuthMode} latencyMs={LatencyMs}",
                user.Id,
                ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "unknown",
                envKey, connectionKey, sbConfig is not null,
                sbConfig?.AuthMode.ToString() ?? "n/a",
                sw.ElapsedMilliseconds);
            // IMPORTANT: connection string value is never logged

            if (sbConfig is null)
                return Results.NotFound(new { error = $"No Service Bus connection configured for key '{connectionKey}' in environment '{envKey}'" });

            // For AzureAd auth mode, never return a connection string.
            var dto = new ServiceBusConfigDto(
                ConnectionKey: connectionKey,
                AuthMode: sbConfig.AuthMode.ToString(),
                ConnectionString: sbConfig.AuthMode == ServiceBusAuthMode.AzureAd ? null : sbConfig.ConnectionString,
                FullyQualifiedNamespace: sbConfig.FullyQualifiedNamespace,
                ManagedIdentityClientId: sbConfig.ManagedIdentityClientId);

            return Results.Ok(dto);
        });

        // GET /api/environments/{envKey}/connections/db
        group.MapGet("/db", (
            string envKey,
            IEnvironmentResolver envResolver,
            HttpContext ctx) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null)
                return Results.Unauthorized();

            var env = envResolver.Resolve(envKey);
            if (!env.AllowAgentConnectionResolution)
                return Results.Problem(
                    title: "Connection resolution disabled",
                    detail: $"env '{envKey}' has agent connection resolution disabled - run from server or set AllowAgentConnectionResolution=true",
                    statusCode: 403);

            var keys = envResolver.ListDbConnectionKeys(envKey);
            return Results.Ok(new { envKey, keys });
        });

        // GET /api/environments/{envKey}/connections/servicebus
        group.MapGet("/servicebus", (
            string envKey,
            IEnvironmentResolver envResolver,
            HttpContext ctx) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null)
                return Results.Unauthorized();

            var env = envResolver.Resolve(envKey);
            if (!env.AllowAgentConnectionResolution)
                return Results.Problem(
                    title: "Connection resolution disabled",
                    detail: $"env '{envKey}' has agent connection resolution disabled - run from server or set AllowAgentConnectionResolution=true",
                    statusCode: 403);

            var keys = envResolver.ListServiceBusConnectionKeys(envKey);
            return Results.Ok(new { envKey, keys });
        });

        return group;
    }

    private static string DetermineDbSource(
        IEnvironmentResolver envResolver, string connectionKey, string envKey)
    {
        var env = envResolver.Resolve(envKey);
        if (env.DbConnections.ContainsKey(connectionKey))
            return "Environment";
        if (connectionKey == "BravoDb" && !string.IsNullOrEmpty(env.BravoDbConnectionString))
            return "Legacy";
        return "TopLevel";
    }
}

// Response models

public record DbConnectionResponse(string ConnectionKey, string ConnectionString, string Source);

public record ServiceBusConfigDto(
    string ConnectionKey,
    string AuthMode,
    string? ConnectionString,
    string? FullyQualifiedNamespace,
    string? ManagedIdentityClientId);
