namespace WUWAScope.Simulation.Models;

public enum NodeRole
{
    Client,
    Coordinator,
    Worker,
    Process,
    Account,
    RaftServer
}

public enum NodeHealthStatus
{
    Idle,
    Healthy,
    Processing,
    Suspected,
    Failed,
    Recovering
}

public enum ModuleStatus
{
    Idle,
    Queued,
    Processing,
    Healthy,
    Suspected,
    Failed,
    Recovering
}

public enum SimulationTaskStatus
{
    Queued,
    Dispatched,
    Running,
    Completed,
    Failed
}

public enum AttemptStatus
{
    Dispatched,
    Running,
    Completed,
    Lost,
    StaleIgnored
}

public enum MessageKind
{
    JobSubmit,
    TaskDispatch,
    TaskResult,
    Heartbeat,
    ClientResponse,
    PeerMessage,
    Transfer,
    SnapshotMarker,
    RequestVote,
    VoteGranted,
    VoteDenied,
    AppendEntries
}

public enum MessageDeliveryStatus
{
    InFlight,
    Delivered,
    Dropped
}

public enum EventCategory
{
    Jobs,
    Network,
    Heartbeats,
    Faults,
    Protocol
}

public enum SimulationEventType
{
    // Task Scheduling
    JobSubmitted,
    JobReceived,
    TaskEnqueued,
    TaskDispatched,
    MessageSent,
    MessageDelivered,
    MessageDropped,
    TaskStarted,
    TaskExecutionFinished,
    ResultReceived,
    HeartbeatSent,
    HeartbeatReceived,
    WorkerTimedOut,
    WorkerCrashed,
    WorkerRecovered,
    TaskRequeued,
    StaleResultIgnored,
    JobFinished,
    ClientResponded,

    // Logical Clocks (Lamport & Vector)
    LocalEvent,
    LogicalMessageSent,
    LogicalMessageReceived,

    // Global Snapshots (Chandy-Lamport)
    TransferSent,
    TransferReceived,
    SnapshotInitiated,
    LocalStateRecorded,
    MarkerSent,
    MarkerReceived,
    ChannelClosed,
    SnapshotCompleted,

    // Raft Leader Election
    ElectionTimeoutFired,
    CandidateStartedElection,
    VoteRequested,
    VoteGranted,
    VoteDenied,
    LeaderElected,
    LeaderSteppedDown,
    AppendEntriesSent,
    AppendEntriesReceived
}
