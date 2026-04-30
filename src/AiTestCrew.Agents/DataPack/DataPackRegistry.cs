using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AiTestCrew.Agents.DataPack;

/// <summary>
/// Walks the packaged datapacks tree and produces an ordered execution plan.
///
/// Expected layout:
/// <code>
/// {root}/{phase}/{envKey}/{NN.subfolder-name}/{NN.script-name}.sql
/// </code>
/// where <c>{phase}</c> is one of <c>datateardown</c> or <c>datapreparation</c>
/// (datateardown runs first so installed procs are available to preparation
/// scripts that EXEC them).
///
/// Folders and files with a leading <c>NN.</c> numeric prefix sort by the
/// integer value of the prefix (so <c>2.foo</c> precedes <c>10.bar</c>).
/// Names without a numeric prefix sort to the end with a one-time WARN log.
///
/// Pure file-system + sorting; no SQL connection. The runner applies per-env
/// opt-in and connection-string checks downstream.
/// </summary>
internal static class DataPackRegistry
{
    // Phase order is fixed: install/refresh stored procedures and run cleanup
    // first, then run preparation seeding (which may EXEC the just-installed procs).
    public static readonly IReadOnlyList<string> PhaseOrder =
        new[] { "datateardown", "datapreparation" };

    private static readonly Regex PrefixRx =
        new(@"^(?<n>\d+)\.\s*(?<rest>.+)$", RegexOptions.Compiled);

    public static DataPackPlan Discover(string rootAbsolute, ILogger logger)
    {
        if (!Directory.Exists(rootAbsolute))
        {
            logger.LogInformation(
                "DataPackRegistry: root '{Root}' does not exist — empty plan.", rootAbsolute);
            return new DataPackPlan(Array.Empty<DataPackEnvPlan>());
        }

        // env -> phase -> phase plan. Discover all envs across both phases first
        // so a single env that has both datateardown and datapreparation content
        // ends up as one DataPackEnvPlan.
        var envToPhases = new SortedDictionary<string, List<DataPackPhasePlan>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var phase in PhaseOrder)
        {
            var phaseDir = Path.Combine(rootAbsolute, phase);
            if (!Directory.Exists(phaseDir)) continue;

            foreach (var envDir in Directory.GetDirectories(phaseDir))
            {
                var envKey = Path.GetFileName(envDir);
                if (string.IsNullOrEmpty(envKey)) continue;

                var scripts = DiscoverScriptsForEnv(envDir, rootAbsolute, logger);
                if (scripts.Count == 0) continue;

                if (!envToPhases.TryGetValue(envKey, out var phases))
                {
                    phases = new List<DataPackPhasePlan>();
                    envToPhases[envKey] = phases;
                }
                phases.Add(new DataPackPhasePlan(phase, scripts));
            }
        }

        // Re-sort each env's phases into the canonical PhaseOrder (the SortedDictionary
        // gives us deterministic env order; this preserves phase order).
        var envs = envToPhases
            .Select(kvp => new DataPackEnvPlan(
                kvp.Key,
                kvp.Value
                    .OrderBy(p => PhaseOrder.ToList().IndexOf(p.Name))
                    .ToArray()))
            .ToArray();

        return new DataPackPlan(envs);
    }

    private static IReadOnlyList<DataPackScript> DiscoverScriptsForEnv(
        string envDir, string rootAbsolute, ILogger logger)
    {
        var subfolders = Directory.GetDirectories(envDir);
        var ordered = OrderByNumericPrefix(subfolders, logger, "subfolder");

        var scripts = new List<DataPackScript>();
        foreach (var subfolder in ordered)
        {
            var subfolderName = Path.GetFileName(subfolder) ?? "";
            var sqlFiles = Directory.GetFiles(subfolder, "*.sql", SearchOption.TopDirectoryOnly);
            var orderedFiles = OrderByNumericPrefix(sqlFiles, logger, "script");

            foreach (var file in orderedFiles)
            {
                var rel = Path.GetRelativePath(rootAbsolute, file);
                scripts.Add(new DataPackScript(file, subfolderName, rel));
            }
        }
        return scripts;
    }

    private static IReadOnlyList<string> OrderByNumericPrefix(
        IReadOnlyList<string> paths, ILogger logger, string label)
    {
        return paths
            .Select(p =>
            {
                var name = Path.GetFileName(p) ?? "";
                var m = PrefixRx.Match(name);
                int prefix = int.MaxValue;
                if (m.Success && int.TryParse(m.Groups["n"].Value, out var n))
                {
                    prefix = n;
                }
                else
                {
                    logger.LogWarning(
                        "DataPackRegistry: {Label} '{Name}' has no numeric prefix — running last.",
                        label, name);
                }
                return (Path: p, Prefix: prefix, Name: name);
            })
            .OrderBy(t => t.Prefix)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Path)
            .ToArray();
    }
}
