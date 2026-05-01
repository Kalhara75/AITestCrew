namespace AiTestCrew.Core.Models;

/// <summary>
/// Restricts a <c>VerifyOnly</c> run to a single post-step inside a single
/// objective. Identifies the post-step by the same coordinates
/// <c>TestObjective.EnumerateAllPostSteps</c> emits, so the filter is
/// stable as long as the parent's step list and the post-step list aren't
/// reordered. Indices are 0-based on the wire (CLI converts from 1-based).
///
/// <see cref="ParentKind"/> values: <c>"Api"</c>, <c>"WebUi"</c>,
/// <c>"DesktopUi"</c>, <c>"AseXml"</c>, <c>"AseXmlDeliver"</c>.
/// </summary>
public sealed record VerifyStepFilter(
    string ParentKind,
    int ParentStepIndex,
    int PostStepIndex);
