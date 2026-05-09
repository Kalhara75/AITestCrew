namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Definition of an Azure Service Bus event-assertion post-step. Receives
/// messages from a queue or topic+subscription, evaluates each against
/// <see cref="Criteria"/>, resolves an overall verdict via
/// <see cref="MatchMode"/>, and (on green) captures values from the first
/// passing message into the post-step run context for sibling post-steps to
/// read as <c>{{Token}}</c>.
///
/// <para>
/// Used as a <see cref="AiTestCrew.Agents.AseXmlAgent.VerificationStep.EventAssert"/>
/// carrier on a post-step — never as a top-level test step. Same shape rule
/// REQ-002 locked in for <c>DbCheckAgent</c>: an event assertion without a
/// preceding action that should have caused the event has no signal.
/// </para>
/// </summary>
public class EventAssertStepDefinition
{
    /// <summary>Short human-readable name (e.g. "MeterReadingCreated event raised").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Logical connection key resolved via
    /// <c>IEnvironmentResolver.ResolveServiceBusConnection(connectionKey, envKey)</c>
    /// against per-env or top-level <c>ServiceBusConnections</c>. Unknown key
    /// surfaces <c>TestStatus.Error</c> at runtime (config issue, not data
    /// issue).
    /// </summary>
    public string ConnectionKey { get; set; } = "";

    /// <summary>Queue or topic+subscription to receive from. Required.</summary>
    public ServiceBusEntity Entity { get; set; } = new();

    /// <summary>How to interpret the message body when evaluating <c>Body.*</c> / <c>BodyXml.*</c> field paths. Defaults to <see cref="BodyFormat.Auto"/>.</summary>
    public BodyFormat BodyFormat { get; set; } = BodyFormat.Auto;

    /// <summary>Receive mode. Defaults to <see cref="ReceiveMode.PeekLock"/> (safe for shared subs).</summary>
    public ReceiveMode ReceiveMode { get; set; } = ReceiveMode.PeekLock;

    /// <summary>How to fold the per-message pass/fail vector into a final verdict. Defaults to <see cref="EventAssertAgent.MatchMode.AnyMessage"/>.</summary>
    public MatchMode MatchMode { get; set; } = MatchMode.AnyMessage;

    /// <summary>
    /// For <see cref="EventAssertAgent.MatchMode.ExactCount"/> /
    /// <see cref="EventAssertAgent.MatchMode.MinCount"/> /
    /// <see cref="EventAssertAgent.MatchMode.MaxCount"/>: the count threshold.
    /// For <see cref="EventAssertAgent.MatchMode.CountRange"/>: the lower
    /// bound (inclusive). Use <c>MaxCount=0</c> for negative-assertion shape
    /// ("verify NO matching event was raised").
    /// </summary>
    public int? ExpectedCount { get; set; }

    /// <summary>For <see cref="EventAssertAgent.MatchMode.CountRange"/>: the upper bound (inclusive).</summary>
    public int? MaxCount { get; set; }

    /// <summary>Total receive window (seconds) after the parent step completes. Default 30.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Hard cap on the number of messages drained for evaluation. Default 50. Prevents a busy queue from running away.</summary>
    public int MaxMessages { get; set; } = 50;

    /// <summary>
    /// When true, the orchestrator drains the entity in
    /// <see cref="ReceiveMode.ReceiveAndDelete"/> mode BEFORE the parent step
    /// runs (2s idle window OR 10s ceiling). Stops stale messages from prior
    /// failed runs from contaminating <see cref="EventAssertAgent.MatchMode.ExactlyOne"/>
    /// / <see cref="EventAssertAgent.MatchMode.MaxCount"/> assertions.
    /// </summary>
    public bool DrainBeforeParent { get; set; }

    /// <summary>
    /// On <see cref="ReceiveMode.PeekLock"/>: when true (default), settle
    /// passing messages with Complete on green; otherwise Abandon all
    /// (debug-friendly — the next attempt sees the same population). On
    /// red, all messages are abandoned regardless.
    /// </summary>
    public bool CompleteOnPass { get; set; } = true;

    /// <summary>
    /// Optional pre-filter on <c>CorrelationId</c>: messages whose CorrelationId
    /// doesn't equal this value (after <c>{{Token}}</c> substitution) are
    /// skipped without evaluating criteria. Useful for narrowing a busy
    /// shared queue down to the messages relevant to a specific test run.
    /// </summary>
    public string? CorrelationFilter { get; set; }

    /// <summary>Optional session ID for session-aware receivers. Single-session only in v1. <c>{{Token}}</c>-substituted.</summary>
    public string? SessionId { get; set; }

    /// <summary>Per-message criteria. Every entry must pass for a message to be considered passing.</summary>
    public List<EventCriterion> Criteria { get; set; } = [];

    /// <summary>Captures from the first passing message. Bind into <c>{{Token}}</c> for sibling post-steps.</summary>
    public List<EventCapture> Captures { get; set; } = [];
}
