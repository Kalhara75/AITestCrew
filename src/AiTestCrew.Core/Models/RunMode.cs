namespace AiTestCrew.Core.Models;

/// <summary>
/// Controls how the test runner sources test cases.
/// </summary>
public enum RunMode
{
    /// <summary>Generate test cases via LLM, save to disk, execute.</summary>
    Normal,

    /// <summary>Load previously saved test cases from disk and re-execute without LLM generation.</summary>
    Reuse,

    /// <summary>Regenerate test cases via LLM (fresh), overwrite the saved test set, execute.</summary>
    Rebaseline,

    /// <summary>List all saved test sets and exit.</summary>
    List
}
