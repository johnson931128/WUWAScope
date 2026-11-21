namespace WUWAScope.Simulation.Models;

public enum NodeRole
{
    Client,
    Coordinator,
    Worker
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
    ClientResponse
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
    Faults
}

public enum SimulationEventType
{
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
    ClientResponded
}
