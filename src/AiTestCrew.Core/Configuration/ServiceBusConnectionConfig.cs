using System.Text.Json.Serialization;

namespace AiTestCrew.Core.Configuration;

/// <summary>
/// Single Azure Service Bus namespace registration. Bound from
/// <c>TestEnvironment.Environments.&lt;env&gt;.ServiceBusConnections.&lt;key&gt;</c>
/// (per-env override) or <c>TestEnvironment.ServiceBusConnections.&lt;key&gt;</c>
/// (top-level fallback).
///
/// <para>
/// Two auth modes are supported:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ServiceBusAuthMode.ConnectionString"/> —
///     classic shared-access connection string. Set <see cref="ConnectionString"/>;
///     <see cref="FullyQualifiedNamespace"/> is ignored.</description></item>
///   <item><description><see cref="ServiceBusAuthMode.AzureAd"/> — uses
///     <c>DefaultAzureCredential</c> (Azure CLI locally; managed identity in
///     prod). Set <see cref="FullyQualifiedNamespace"/> (e.g.
///     <c>"my-namespace.servicebus.windows.net"</c>); optionally set
///     <see cref="ManagedIdentityClientId"/> when a user-assigned managed
///     identity is required.</description></item>
/// </list>
/// </summary>
public class ServiceBusConnectionConfig
{
    /// <summary>Auth mode. Defaults to <see cref="ServiceBusAuthMode.ConnectionString"/>.</summary>
    public ServiceBusAuthMode AuthMode { get; set; } = ServiceBusAuthMode.ConnectionString;

    /// <summary>SAS / connection string. Required when <see cref="AuthMode"/> is <see cref="ServiceBusAuthMode.ConnectionString"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Fully-qualified Service Bus namespace (e.g. <c>"my-namespace.servicebus.windows.net"</c>).
    /// Required when <see cref="AuthMode"/> is <see cref="ServiceBusAuthMode.AzureAd"/>.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>
    /// Optional user-assigned managed identity client id. When set with
    /// <see cref="AuthMode"/> = <see cref="ServiceBusAuthMode.AzureAd"/>, the
    /// resulting <c>DefaultAzureCredential</c> is constructed with the UAMI
    /// client id pinned. Leave null for system-assigned MI / Azure CLI.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceBusAuthMode
{
    ConnectionString = 0,
    AzureAd,
}
