namespace AiTestCrew.Agents.DataPack;

/// <summary>Internal execution plan emitted by <see cref="DataPackRegistry"/> and consumed by <see cref="DataPackRunner"/>.</summary>
internal sealed record DataPackPlan(IReadOnlyList<DataPackEnvPlan> Envs);

/// <summary>All scripts discovered on disk for one environment, ordered by phase then numeric prefix.</summary>
internal sealed record DataPackEnvPlan(
    string EnvKey,
    IReadOnlyList<DataPackPhasePlan> Phases);

/// <summary>Scripts for a single phase ("datateardown" or "datapreparation"), already sorted.</summary>
internal sealed record DataPackPhasePlan(
    string Name,
    IReadOnlyList<DataPackScript> Scripts);

/// <summary>One .sql file slated for execution.</summary>
internal sealed record DataPackScript(
    string FullPath,
    string Subfolder,
    string RelativePath);
