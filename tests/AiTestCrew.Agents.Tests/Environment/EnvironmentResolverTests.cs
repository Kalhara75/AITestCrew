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
}
