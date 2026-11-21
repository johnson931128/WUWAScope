namespace WUWAScope.Simulation.Models;

public sealed record ModuleLink(
    string Id,
    string SourceModuleId,
    string TargetModuleId,
    string Label,
    bool IsDashed = false);

public sealed record ModuleState(
    string Id,
    string NodeId,
    string Name,
    string ShortRole,
    string PlainDescription,
    ModuleStatus Status,
    double GridX,
    double GridY,
    IReadOnlyList<string> QueueItems,
    IReadOnlyDictionary<string, string> Counters,
    string? ActiveHint = null);

public sealed record NodeState(
    string Id,
    string Name,
    NodeRole Role,
    NodeHealthStatus Health,
    int Capacity,
    int ActiveTaskCount,
    string StatusSummary,
    string PlainDescription,
    double GridX,
    double GridY,
    IReadOnlyList<ModuleState> Modules,
    IReadOnlyList<ModuleLink> InternalLinks,
    IReadOnlyList<string> InboxItems,
    IReadOnlyList<string> OutboxItems);

public sealed record LinkState(
    string Id,
    string SourceNodeId,
    string TargetNodeId,
    int SimulatedLatencyMs,
    string Label,
    bool IsHeartbeatChannel = false,
    bool IsActive = false);

public sealed record MessageState(
    string Id,
    MessageKind Kind,
    string SourceNodeId,
    string TargetNodeId,
    string? TaskId,
    string? AttemptId,
    long SentAtMs,
    long DeliverAtMs,
    MessageDeliveryStatus Status,
    string PillLabel,
    bool IsDelayed = false);

public sealed record JobState(
    string Id,
    IReadOnlyList<string> TaskIds,
    SimulationTaskStatus Status);

public sealed record TaskItemState(
    string Id,
    string JobId,
    SimulationTaskStatus Status,
    string? AssignedWorkerId,
    string? CurrentAttemptId,
    string? AcceptedAttemptId);

public sealed record AttemptState(
    string Id,
    string TaskId,
    string WorkerId,
    int Generation,
    AttemptStatus Status);

public sealed record EventRecord(
    string Id,
    long TimestampMs,
    long Sequence,
    SimulationEventType Type,
    EventCategory Category,
    string SourceNodeId,
    string? TargetNodeId,
    string? ModuleId,
    string? CauseEventId,
    string Summary,
    string PlainExplanation,
    IReadOnlyDictionary<string, string> Payload);

public sealed record SimulatedMetrics(
    long SimulatedTimeMs,
    int QueueDepth,
    int PendingTasks,
    int RunningTasks,
    int CompletedTasks,
    int DispatchedTasks,
    int RetryCount,
    int MessagesSent,
    int MessagesDropped,
    int StaleResultsIgnored = 0,
    long? LastJobDurationMs = null,
    int ArmedDelayCount = 0,
    int ArmedDropCount = 0);

public sealed record SimulationState(
    string ScenarioId,
    string ScenarioTitle,
    bool IsPrototypeStub,
    int Seed,
    long SimulatedTimeMs,
    IReadOnlyList<NodeState> Nodes,
    IReadOnlyList<LinkState> Links,
    IReadOnlyList<MessageState> InFlightMessages,
    IReadOnlyList<JobState> Jobs,
    IReadOnlyList<TaskItemState> Tasks,
    IReadOnlyList<AttemptState> Attempts,
    IReadOnlyList<EventRecord> EventLog,
    SimulatedMetrics Metrics);
