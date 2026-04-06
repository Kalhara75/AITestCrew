namespace AiTestCrew.Core.Interfaces;

using AiTestCrew.Core.Models;

/// <summary>
/// Contract for all test agents in the crew.
/// Each agent handles specific test target types.
/// </summary>
public interface ITestAgent
{
    string Name { get; }
    string Role { get; }

    /// <summary>Can this agent handle the given task?</summary>
    Task<bool> CanHandleAsync(TestTask task);

    /// <summary>
    /// Execute the task and return a single result.
    /// The result contains one TestStep per test case (API call or UI test case).
    /// Metadata["generatedTestCases"] carries the definitions for persistence.
    /// </summary>
    Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default);
}