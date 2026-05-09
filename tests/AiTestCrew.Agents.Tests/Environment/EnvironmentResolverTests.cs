using AiTestCrew.Agents.Environment;
using AiTestCrew.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.Environment;

public class EnvironmentResolverTests
{
    [Fact]
    public void Bravo_db_falls_back_to_legacy_when_dicts_empty()
    {
        var cfg = new TestEnvironmentConfig
        {
            AseXml = new AseXmlConfig
            {
                BravoDb = new BravoDbConfig { ConnectionString = "Server=legacy;Database=bravo;" },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveDbConnectionString("BravoDb", null).Should().Be("Server=legacy;Database=bravo;");
    }

    [Fact]
    public void Bravo_db_per_env_override_wins_over_legacy()
    {
        var cfg = new TestEnvironmentConfig
        {
            AseXml = new AseXmlConfig
            {
                BravoDb = new BravoDbConfig { ConnectionString = "Server=legacy;" },
            },
            Environments =
            {
                ["sumo"] = new EnvironmentConfig
                {
                    DbConnections = { ["BravoDb"] = "Server=sumo-bravo;" },
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveDbConnectionString("BravoDb", "sumo").Should().Be("Server=sumo-bravo;");
    }

    [Fact]
    public void Per_env_override_wins_over_top_level()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["SdrReportingDb"] = "Server=top-level;" },
            Environments =
            {
                ["sumo"] = new EnvironmentConfig
                {
                    DbConnections = { ["SdrReportingDb"] = "Server=sumo-sdr;" },
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveDbConnectionString("SdrReportingDb", "sumo").Should().Be("Server=sumo-sdr;");
    }

    [Fact]
    public void Top_level_used_when_per_env_misses()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["SdrReportingDb"] = "Server=top-level;" },
            Environments = { ["sumo"] = new EnvironmentConfig() },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveDbConnectionString("SdrReportingDb", "sumo").Should().Be("Server=top-level;");
    }

    [Fact]
    public void Unknown_key_returns_null()
    {
        var cfg = new TestEnvironmentConfig
        {
            Environments = { ["sumo"] = new EnvironmentConfig() },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveDbConnectionString("NonExistent", "sumo").Should().BeNull();
    }

    [Fact]
    public void Empty_string_value_falls_through_to_next_tier()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["SdrReportingDb"] = "Server=top-level;" },
            Environments =
            {
                ["sumo"] = new EnvironmentConfig
                {
                    // Whitespace per-env override should NOT mask the top-level entry.
                    DbConnections = { ["SdrReportingDb"] = "   " },
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveDbConnectionString("SdrReportingDb", "sumo").Should().Be("Server=top-level;");
    }

    [Fact]
    public void List_keys_includes_BravoDb_implicitly()
    {
        var cfg = new TestEnvironmentConfig();
        var resolver = new EnvironmentResolver(cfg);

        resolver.ListDbConnectionKeys(null).Should().ContainSingle().Which.Should().Be("BravoDb");
    }

    [Fact]
    public void List_keys_unions_per_env_top_level_and_BravoDb()
    {
        var cfg = new TestEnvironmentConfig
        {
            DbConnections = { ["TopLevelDb"] = "x" },
            Environments =
            {
                ["sumo"] = new EnvironmentConfig
                {
                    DbConnections = { ["SdrReportingDb"] = "y" },
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ListDbConnectionKeys("sumo").Should().BeEquivalentTo(
            new[] { "BravoDb", "SdrReportingDb", "TopLevelDb" });
    }

    [Fact]
    public void Allow_dry_run_denies_unknown_non_null_env_key()
    {
        // Unknown env keys are conservative-deny — typos shouldn't accidentally permit.
        var cfg = new TestEnvironmentConfig
        {
            Environments =
            {
                ["dev"] = new EnvironmentConfig { AllowDbDryRun = true },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveAllowDbDryRun("production-typo").Should().BeFalse();
    }

    [Fact]
    public void Allow_dry_run_defaults_true_when_envs_dict_empty_and_key_null()
    {
        // No Environments configured at all + null key — fall through to the
        // EnvironmentConfig default (true). Preserves legacy single-env behaviour.
        var cfg = new TestEnvironmentConfig();
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveAllowDbDryRun(null).Should().BeTrue();
    }

    [Fact]
    public void Allow_dry_run_honours_per_env_opt_out()
    {
        var cfg = new TestEnvironmentConfig
        {
            Environments =
            {
                ["prod"] = new EnvironmentConfig { AllowDbDryRun = false },
                ["dev"] = new EnvironmentConfig { AllowDbDryRun = true },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveAllowDbDryRun("prod").Should().BeFalse();
        resolver.ResolveAllowDbDryRun("dev").Should().BeTrue();
    }

    // ── REQ-004 Service Bus connection registry ────────────────────────

    [Fact]
    public void ServiceBus_per_env_override_wins_over_top_level()
    {
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                ["DefaultBus"] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "Endpoint=sb://top-level.servicebus.windows.net/;SharedAccessKey=top",
                },
            },
            Environments =
            {
                ["sumo"] = new EnvironmentConfig
                {
                    ServiceBusConnections =
                    {
                        ["DefaultBus"] = new ServiceBusConnectionConfig
                        {
                            AuthMode = ServiceBusAuthMode.AzureAd,
                            FullyQualifiedNamespace = "sumo.servicebus.windows.net",
                        },
                    },
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        var resolved = resolver.ResolveServiceBusConnection("DefaultBus", "sumo");
        resolved.Should().NotBeNull();
        resolved!.AuthMode.Should().Be(ServiceBusAuthMode.AzureAd);
        resolved.FullyQualifiedNamespace.Should().Be("sumo.servicebus.windows.net");
    }

    [Fact]
    public void ServiceBus_top_level_used_when_per_env_misses()
    {
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                ["DefaultBus"] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "Endpoint=sb://top-level.servicebus.windows.net/;SharedAccessKey=top",
                },
            },
            Environments = { ["sumo"] = new EnvironmentConfig() },
        };
        var resolver = new EnvironmentResolver(cfg);

        var resolved = resolver.ResolveServiceBusConnection("DefaultBus", "sumo");
        resolved.Should().NotBeNull();
        resolved!.AuthMode.Should().Be(ServiceBusAuthMode.ConnectionString);
        resolved.ConnectionString.Should().Contain("top-level");
    }

    [Fact]
    public void ServiceBus_unknown_key_returns_null()
    {
        var cfg = new TestEnvironmentConfig
        {
            Environments = { ["sumo"] = new EnvironmentConfig() },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveServiceBusConnection("Missing", "sumo").Should().BeNull();
        resolver.ResolveServiceBusConnection("", "sumo").Should().BeNull();
    }

    [Fact]
    public void ServiceBus_empty_connection_string_falls_through_to_next_tier()
    {
        // A per-env override with an unusable (empty connection string) entry
        // must NOT mask the top-level entry — same fall-through as DbConnections.
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                ["DefaultBus"] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "Endpoint=sb://top-level.servicebus.windows.net/;SharedAccessKey=top",
                },
            },
            Environments =
            {
                ["sumo"] = new EnvironmentConfig
                {
                    ServiceBusConnections =
                    {
                        ["DefaultBus"] = new ServiceBusConnectionConfig
                        {
                            AuthMode = ServiceBusAuthMode.ConnectionString,
                            ConnectionString = "   ",
                        },
                    },
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveServiceBusConnection("DefaultBus", "sumo")!
            .ConnectionString.Should().Contain("top-level");
    }

    [Fact]
    public void ServiceBus_azure_ad_with_no_namespace_is_unusable()
    {
        // AuthMode=AzureAd but FullyQualifiedNamespace is blank — treat as unusable.
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                ["DefaultBus"] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.AzureAd,
                    FullyQualifiedNamespace = "",
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveServiceBusConnection("DefaultBus", null).Should().BeNull();
    }

    [Fact]
    public void ServiceBus_list_keys_unions_per_env_and_top_level()
    {
        var cfg = new TestEnvironmentConfig
        {
            ServiceBusConnections =
            {
                ["DefaultBus"] = new ServiceBusConnectionConfig
                {
                    AuthMode = ServiceBusAuthMode.ConnectionString,
                    ConnectionString = "x",
                },
            },
            Environments =
            {
                ["sumo"] = new EnvironmentConfig
                {
                    ServiceBusConnections =
                    {
                        ["MeterEvents"] = new ServiceBusConnectionConfig
                        {
                            AuthMode = ServiceBusAuthMode.AzureAd,
                            FullyQualifiedNamespace = "y",
                        },
                    },
                },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ListServiceBusConnectionKeys("sumo").Should().BeEquivalentTo(
            new[] { "DefaultBus", "MeterEvents" });
    }

    [Fact]
    public void ServiceBus_list_keys_empty_when_nothing_configured()
    {
        var cfg = new TestEnvironmentConfig();
        var resolver = new EnvironmentResolver(cfg);

        resolver.ListServiceBusConnectionKeys(null).Should().BeEmpty();
    }

    [Fact]
    public void Allow_event_assert_peek_denies_unknown_non_null_env_key()
    {
        var cfg = new TestEnvironmentConfig
        {
            Environments =
            {
                ["dev"] = new EnvironmentConfig { AllowEventAssertPeek = true },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveAllowEventAssertPeek("production-typo").Should().BeFalse();
    }

    [Fact]
    public void Allow_event_assert_peek_defaults_true_when_envs_dict_empty_and_key_null()
    {
        var cfg = new TestEnvironmentConfig();
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveAllowEventAssertPeek(null).Should().BeTrue();
    }

    [Fact]
    public void Allow_event_assert_peek_honours_per_env_opt_out()
    {
        var cfg = new TestEnvironmentConfig
        {
            Environments =
            {
                ["prod"] = new EnvironmentConfig { AllowEventAssertPeek = false },
                ["dev"] = new EnvironmentConfig { AllowEventAssertPeek = true },
            },
        };
        var resolver = new EnvironmentResolver(cfg);

        resolver.ResolveAllowEventAssertPeek("prod").Should().BeFalse();
        resolver.ResolveAllowEventAssertPeek("dev").Should().BeTrue();
    }
}
