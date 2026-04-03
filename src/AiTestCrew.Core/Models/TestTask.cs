using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AiTestCrew.Core.Models;

/// <summary>
/// A single test task that gets routed to an agent.
/// Created by the orchestrator when decomposing an objective.
/// </summary>
public class TestTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public required string Description { get; init; }
    public required TestTargetType Target { get; init; }
    public TestPriority Priority { get; init; } = TestPriority.Normal;
    public Dictionary<string, object> Parameters { get; init; } = [];
    public List<string> DependsOn { get; init; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}