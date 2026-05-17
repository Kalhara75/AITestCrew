namespace AiTestCrew.Core.Capabilities;

/// <summary>
/// Structured representation of AITestCrew current testing capabilities.
/// Returned by GET /api/capabilities and included as context in Xray import LLM prompts.
/// </summary>
public class CapabilityRegistryDto
{
    public List<string> StepTypes { get; set; } = [];
    public List<string> PostStepTypes { get; set; } = [];
    public List<string> AssertionPrimitives { get; set; } = [];
    public List<string> UnsupportedExamples { get; set; } = [];
}