namespace AiTestCrew.Agents.EventAssertAgent.Body;

/// <summary>
/// Tri-state result of resolving a body / field path on a received Service
/// Bus message — <em>found a non-null value</em>, <em>found JSON null
/// (or equivalent)</em>, <em>extraction failed with a typed reason</em>.
/// Mirrors REQ-002's <see cref="DbAgent.JsonValueExtractor.ExtractionStatus"/>
/// distinction so criteria semantics line up between DB and event asserts.
/// </summary>
public readonly record struct ExtractResult(
    ExtractStatus Status,
    string? Value,
    string? Error)
{
    public static ExtractResult FoundValue(string value) => new(ExtractStatus.Found, value, null);
    public static ExtractResult FoundNullValue() => new(ExtractStatus.FoundNull, null, null);
    public static ExtractResult Failed(string error) => new(ExtractStatus.Failed, null, error);
}

public enum ExtractStatus
{
    /// <summary>Path resolved to a non-null value (in <see cref="ExtractResult.Value"/>).</summary>
    Found,
    /// <summary>Path resolved, but to a null / DBNull / missing-but-explicit value. Treated like SQL NULL by IsNull / IsNotNull operators.</summary>
    FoundNull,
    /// <summary>Path could not be resolved (missing JSON path, malformed body, binary body for Body.* path, …). <see cref="ExtractResult.Error"/> is set.</summary>
    Failed,
}
