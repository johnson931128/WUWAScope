using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Stubs;

public static class ScenarioCatalog
{
    public const string CourseIntroUrl =
        "https://github.com/johnson931128/Distributed-System/blob/main/notes/00-course-introduction.md";

    public const string LogicalClocksUrl =
        "https://github.com/johnson931128/Distributed-System/blob/main/notes/01-logical-clocks-checkpointing.md";

    public const string FaultToleranceUrl =
        "https://github.com/johnson931128/Distributed-System/blob/main/notes/02-fault-tolerance-paxos-raft.md";

    public static IReadOnlyList<ScenarioDescriptor> All { get; } =
    [
        new ScenarioDescriptor(
            Id: "task-scheduling",
            Title: "Distributed Task Scheduling",
            ShortSummary: "Watch 1 Client, 1 Coordinator, and 3 Workers schedule tasks, send heartbeats, and recover from crashes.",
            IsAvailable: true,
            TheoryNoteTitle: "Course Introduction & Fault Tolerance Notes",
            TheoryNoteUrl: FaultToleranceUrl,
            SimplificationsNote: "Teaching model: machines run inside one local simulator with predictable network delays instead of real OS processes or physical cables. Metrics are 100% simulated."),
        new ScenarioDescriptor(
            Id: "logical-clocks",
            Title: "Logical Clocks (Lamport & Vector)",
            ShortSummary: "Order events across 3 processes without synchronized wall clocks, and tell causality (a → b) apart from concurrency (a ∥ b).",
            IsAvailable: true,
            TheoryNoteTitle: "Chapter 01 — Logical Clocks & Vector Clocks",
            TheoryNoteUrl: LogicalClocksUrl,
            SimplificationsNote: "Teaching model based on Distributed-System notes/01 §3–4: fixed 3-process group (P1, P2, P3), reliable FIFO links, no process restarts or hidden side channels."),
        new ScenarioDescriptor(
            Id: "global-snapshots",
            Title: "Global Snapshots (Chandy-Lamport)",
            ShortSummary: "Capture a consistent snapshot of 3 bank accounts ($1,000 total) using Marker messages while $50 transfers are still in flight.",
            IsAvailable: true,
            TheoryNoteTitle: "Chapter 01 — Chandy-Lamport Distributed Snapshot",
            TheoryNoteUrl: LogicalClocksUrl,
            SimplificationsNote: "Teaching model based on Distributed-System notes/01 §6: reliable FIFO channels with no node crashes during a snapshot. Demonstrates why recorded local balances + recorded in-flight channels conserve total money ($1,000)."),
        new ScenarioDescriptor(
            Id: "raft-leader-election",
            Title: "Leader Election (Teaching Raft)",
            ShortSummary: "Watch 5 voting servers elect a Leader with randomized timeouts, Terms, and majority quorums (3 of 5), and fail over when the Leader crashes.",
            IsAvailable: true,
            TheoryNoteTitle: "Chapter 02 — Fault Tolerance & Raft Leader Election",
            TheoryNoteUrl: FaultToleranceUrl,
            SimplificationsNote: "Teaching model based on Distributed-System notes/02 §11.1–11.2: focuses on Terms, randomized election timeouts, RequestVote, majority quorum (3/5), higher-term step-down, and crash/recovery. Full log replication and membership changes are intentionally omitted.")
    ];
}

/// <summary>
/// Phase 0 prototype stub data provider.
/// Supplies a clearly labeled sample snapshot of the 5-node cluster so the desktop UI,
/// 2.5D isometric canvas, Cluster -> Node -> Module drill-down, inspector, and timeline
/// can be verified before the Phase 1 discrete-event engine is connected.
/// </summary>
public sealed class PrototypeStubEngine : ISimulationEngine
{
    private readonly ScenarioConfig _config;
    private readonly int _seed;
    private SimulationState _state;

    public PrototypeStubEngine(ScenarioConfig? config = null, int seed = 42)
    {
        _config = config ?? new ScenarioConfig();
        _seed = seed;
        _state = BuildSampleState(_config, _seed);
    }

    public bool IsPrototypeStub => true;

    public event EventHandler<SimulationState>? StateChanged;

    public SimulationState GetState() => _state;

    public long? PeekNextEventTimeMs() => null;

    public StepResult Step()
    {
        return new StepResult(
            Executed: false,
            Event: null,
            State: _state,
            Note: "Prototype shell (Phase 0): live event stepping arrives in Phase 1.");
    }

    public SimulationState Reset()
    {
        _state = BuildSampleState(_config, _seed);
        StateChanged?.Invoke(this, _state);
        return _state;
    }

    public void DispatchAction(UserAction action)
    {
        // Intentionally no-op in Phase 0 prototype stub so UI never fakes live simulation transitions.
        _ = action;
    }

    private static SimulationState BuildSampleState(ScenarioConfig config, int seed)
    {
        var clientModules = new List<ModuleState>
        {
            new(
                Id: "client-submitter",
                NodeId: "client",
                Name: "Job Submitter",
                ShortRole: "Creates new work requests",
                PlainDescription: "Packages a user's job request with a stable Job ID and hands it to the Outbox so it can be sent to the Coordinator.",
                Status: ModuleStatus.Healthy,
                GridX: 0.0,
                GridY: 0.0,
                QueueItems: ["Job J1 (2 tasks) -> sent"],
                Counters: new Dictionary<string, string>
                {
                    ["Jobs Submitted (Simulated)"] = "1",
                    ["Pending Replies (Simulated)"] = "1"
                },
                ActiveHint: "Sent J1 at t=0 ms"),
            new(
                Id: "client-outbox",
                NodeId: "client",
                Name: "Outbox",
                ShortRole: "Sends requests to the network",
                PlainDescription: "Holds outgoing job messages briefly before putting them onto the simulated network link toward the Coordinator.",
                Status: ModuleStatus.Idle,
                GridX: 2.8,
                GridY: 0.0,
                QueueItems: [],
                Counters: new Dictionary<string, string>
                {
                    ["Messages Sent (Simulated)"] = "1"
                }),
            new(
                Id: "client-inbox",
                NodeId: "client",
                Name: "Response Inbox",
                ShortRole: "Receives finished results",
                PlainDescription: "Listens for completion replies coming back from the Coordinator once all tasks in a job finish.",
                Status: ModuleStatus.Idle,
                GridX: 2.8,
                GridY: 2.6,
                QueueItems: [],
                Counters: new Dictionary<string, string>
                {
                    ["Replies Received (Simulated)"] = "0"
                }),
            new(
                Id: "client-tracker",
                NodeId: "client",
                Name: "Job Tracker",
                ShortRole: "Tracks open jobs",
                PlainDescription: "Remembers which jobs are still running and marks a job complete when its final result arrives.",
                Status: ModuleStatus.Processing,
                GridX: 0.0,
                GridY: 2.6,
                QueueItems: ["J1: Waiting for Coordinator reply"],
                Counters: new Dictionary<string, string>
                {
                    ["Open Jobs (Simulated)"] = "1",
                    ["Completed Jobs (Simulated)"] = "0"
                },
                ActiveHint: "Tracking Job J1")
        };

        var clientLinks = new List<ModuleLink>
        {
            new("cl-1", "client-submitter", "client-outbox", "Job J1"),
            new("cl-2", "client-inbox", "client-tracker", "Final Result", IsDashed: true)
        };

        var coordModules = new List<ModuleState>
        {
            new(
                Id: "coord-req-handler",
                NodeId: "coordinator",
                Name: "Request Handler",
                ShortRole: "Accepts client jobs",
                PlainDescription: "Receives incoming jobs from the Client, splits each job into tracked tasks, and hands them to the Job Queue.",
                Status: ModuleStatus.Healthy,
                GridX: 0.0,
                GridY: 0.0,
                QueueItems: ["Processed Job J1 -> created T1, T2"],
                Counters: new Dictionary<string, string>
                {
                    ["Jobs Accepted (Simulated)"] = "1",
                    ["Tasks Created (Simulated)"] = "2"
                }),
            new(
                Id: "coord-job-queue",
                NodeId: "coordinator",
                Name: "Job Queue",
                ShortRole: "Holds waiting tasks",
                PlainDescription: "A first-in, first-out waiting line for tasks that have not been sent to a Worker yet — or were put back after a Worker failed.",
                Status: ModuleStatus.Queued,
                GridX: 2.9,
                GridY: 0.0,
                QueueItems: ["Task T2 (Job J1) - Waiting for Worker"],
                Counters: new Dictionary<string, string>
                {
                    ["Queue Depth (Simulated)"] = "1",
                    ["Total Enqueued (Simulated)"] = "2"
                },
                ActiveHint: "1 task waiting"),
            new(
                Id: "coord-scheduler",
                NodeId: "coordinator",
                Name: "Scheduler",
                ShortRole: "Picks a healthy worker",
                PlainDescription: "Checks the Worker Registry to find an alive worker with free capacity, stamps a new Attempt ID (like A1), and schedules dispatch.",
                Status: ModuleStatus.Processing,
                GridX: 5.8,
                GridY: 0.0,
                QueueItems: ["Dispatched T1 -> Worker 1 (Attempt A1)"],
                Counters: new Dictionary<string, string>
                {
                    ["Dispatches (Simulated)"] = "1",
                    ["Retries (Simulated)"] = "0"
                },
                ActiveHint: "Assigning T2 next"),
            new(
                Id: "coord-outbox",
                NodeId: "coordinator",
                Name: "Outbox",
                ShortRole: "Sends dispatches & replies",
                PlainDescription: "Places task assignments onto links heading to Workers and sends final job summaries back to the Client.",
                Status: ModuleStatus.Healthy,
                GridX: 8.7,
                GridY: 0.0,
                QueueItems: ["Dispatch T1 (A1) -> Worker 1"],
                Counters: new Dictionary<string, string>
                {
                    ["Messages Sent (Simulated)"] = "1"
                }),
            new(
                Id: "coord-hb-monitor",
                NodeId: "coordinator",
                Name: "Heartbeat Monitor",
                ShortRole: "Watches worker health",
                PlainDescription: "Listens for periodic 'I am alive' heartbeats from every Worker. If a Worker stays silent past the timeout, this module triggers reassignment.",
                Status: ModuleStatus.Healthy,
                GridX: 2.9,
                GridY: 3.0,
                QueueItems: ["W1: HB @ 100 ms", "W2: HB @ 110 ms", "W3: HB @ 120 ms"],
                Counters: new Dictionary<string, string>
                {
                    ["Timeout Policy (Simulated)"] = $"{config.HeartbeatTimeoutMs} ms",
                    ["Timed Out Workers"] = "0"
                }),
            new(
                Id: "coord-registry",
                NodeId: "coordinator",
                Name: "Worker Registry",
                ShortRole: "Directory of workers",
                PlainDescription: "Stores each Worker's latest health status, last heartbeat time, and whether it currently has room to take another task.",
                Status: ModuleStatus.Healthy,
                GridX: 5.8,
                GridY: 3.0,
                QueueItems: ["Worker 1: Busy (T1/A1)", "Worker 2: Idle (Free)", "Worker 3: Idle (Free)"],
                Counters: new Dictionary<string, string>
                {
                    ["Healthy Workers"] = "3 / 3",
                    ["Free Slots"] = "2"
                }),
            new(
                Id: "coord-result-handler",
                NodeId: "coordinator",
                Name: "Result Handler",
                ShortRole: "Checks & accepts results",
                PlainDescription: "Checks each incoming result's Attempt ID so outdated late replies from an old attempt can never overwrite a newer accepted result.",
                Status: ModuleStatus.Idle,
                GridX: 8.7,
                GridY: 3.0,
                QueueItems: ["No completed results yet"],
                Counters: new Dictionary<string, string>
                {
                    ["Accepted Results"] = "0",
                    ["Stale Results Ignored"] = "0"
                })
        };

        var coordLinks = new List<ModuleLink>
        {
            new("co-1", "coord-req-handler", "coord-job-queue", "Enqueue"),
            new("co-2", "coord-job-queue", "coord-scheduler", "Next Task"),
            new("co-3", "coord-scheduler", "coord-outbox", "Dispatch A1"),
            new("co-4", "coord-hb-monitor", "coord-registry", "Update Health", IsDashed: true),
            new("co-5", "coord-registry", "coord-scheduler", "Healthy List", IsDashed: true),
            new("co-6", "coord-result-handler", "coord-outbox", "Client Reply", IsDashed: true)
        };

        var worker1 = BuildWorkerNode(
            id: "worker-1",
            name: "Worker 1",
            health: NodeHealthStatus.Processing,
            gridX: 3.1,
            gridY: -1.6,
            activeTask: "T1 (Attempt A1)",
            statusSummary: "Running Task T1 (Attempt A1)",
            lastHbMs: 100);

        var worker2 = BuildWorkerNode(
            id: "worker-2",
            name: "Worker 2",
            health: NodeHealthStatus.Healthy,
            gridX: 4.1,
            gridY: 0.7,
            activeTask: null,
            statusSummary: "Healthy · Idle (Ready for work)",
            lastHbMs: 110);

        var worker3 = BuildWorkerNode(
            id: "worker-3",
            name: "Worker 3",
            health: NodeHealthStatus.Healthy,
            gridX: 3.4,
            gridY: 3.3,
            activeTask: null,
            statusSummary: "Healthy · Idle (Ready for work)",
            lastHbMs: 120);

        var nodes = new List<NodeState>
        {
            new(
                Id: "client",
                Name: "Client",
                Role: NodeRole.Client,
                Health: NodeHealthStatus.Healthy,
                Capacity: 1,
                ActiveTaskCount: 0,
                StatusSummary: "Waiting for Job J1 result",
                PlainDescription: "The user's machine that sends jobs into the cluster and waits for final answers.",
                GridX: -2.6,
                GridY: 0.5,
                Modules: clientModules,
                InternalLinks: clientLinks,
                InboxItems: [],
                OutboxItems: ["Job J1 (sent @ 0 ms)"]),
            new(
                Id: "coordinator",
                Name: "Coordinator",
                Role: NodeRole.Coordinator,
                Health: NodeHealthStatus.Processing,
                Capacity: 4,
                ActiveTaskCount: 1,
                StatusSummary: "1 task queued · 1 running on W1",
                PlainDescription: "The central organizer. It accepts jobs, keeps the waiting queue, tracks worker heartbeats, and reassigns work if a worker fails.",
                GridX: 0.2,
                GridY: 0.4,
                Modules: coordModules,
                InternalLinks: coordLinks,
                InboxItems: ["Job J1", "Heartbeat (W1, W2, W3)"],
                OutboxItems: ["Dispatch T1 (A1) -> Worker 1"]),
            worker1,
            worker2,
            worker3
        };

        var links = new List<LinkState>
        {
            new("link-c-coord", "client", "coordinator", config.DefaultNetworkLatencyMs, "Submit Job J1", IsHeartbeatChannel: false, IsActive: true),
            new("link-coord-w1", "coordinator", "worker-1", config.DefaultNetworkLatencyMs, "Dispatch T1 · A1", IsHeartbeatChannel: false, IsActive: true),
            new("link-coord-w2", "coordinator", "worker-2", config.DefaultNetworkLatencyMs, "Heartbeat · OK", IsHeartbeatChannel: true, IsActive: false),
            new("link-coord-w3", "coordinator", "worker-3", config.DefaultNetworkLatencyMs, "Heartbeat · OK", IsHeartbeatChannel: true, IsActive: false)
        };

        var messages = new List<MessageState>
        {
            new(
                Id: "msg-1",
                Kind: MessageKind.TaskDispatch,
                SourceNodeId: "coordinator",
                TargetNodeId: "worker-1",
                TaskId: "T1",
                AttemptId: "A1",
                SentAtMs: 50,
                DeliverAtMs: 130,
                Status: MessageDeliveryStatus.InFlight,
                PillLabel: "Dispatch T1 · A1"),
            new(
                Id: "msg-2",
                Kind: MessageKind.Heartbeat,
                SourceNodeId: "worker-2",
                TargetNodeId: "coordinator",
                TaskId: null,
                AttemptId: null,
                SentAtMs: 110,
                DeliverAtMs: 190,
                Status: MessageDeliveryStatus.InFlight,
                PillLabel: "Heartbeat · W2")
        };

        var jobs = new List<JobState>
        {
            new("J1", ["T1", "T2"], SimulationTaskStatus.Running)
        };

        var tasks = new List<TaskItemState>
        {
            new("T1", "J1", SimulationTaskStatus.Running, "worker-1", "A1", null),
            new("T2", "J1", SimulationTaskStatus.Queued, null, null, null)
        };

        var attempts = new List<AttemptState>
        {
            new("A1", "T1", "worker-1", 1, AttemptStatus.Running)
        };

        var events = new List<EventRecord>
        {
            new(
                Id: "evt-1",
                TimestampMs: 0,
                Sequence: 1,
                Type: SimulationEventType.JobSubmitted,
                Category: EventCategory.Jobs,
                SourceNodeId: "client",
                TargetNodeId: "coordinator",
                ModuleId: "client-submitter",
                CauseEventId: null,
                Summary: "Client submitted Job J1 (2 tasks)",
                PlainExplanation: "The Client asked the cluster to run Job J1, which contains two independent tasks (T1 and T2).",
                Payload: new Dictionary<string, string> { ["JobId"] = "J1", ["Tasks"] = "T1, T2" }),
            new(
                Id: "evt-2",
                TimestampMs: 40,
                Sequence: 2,
                Type: SimulationEventType.TaskEnqueued,
                Category: EventCategory.Jobs,
                SourceNodeId: "coordinator",
                TargetNodeId: null,
                ModuleId: "coord-job-queue",
                CauseEventId: "evt-1",
                Summary: "Coordinator queued T1 and T2",
                PlainExplanation: "The Request Handler accepted Job J1 and placed its tasks into the Coordinator's Job Queue.",
                Payload: new Dictionary<string, string> { ["JobId"] = "J1", ["QueueDepth"] = "2" }),
            new(
                Id: "evt-3",
                TimestampMs: 50,
                Sequence: 3,
                Type: SimulationEventType.TaskDispatched,
                Category: EventCategory.Network,
                SourceNodeId: "coordinator",
                TargetNodeId: "worker-1",
                ModuleId: "coord-scheduler",
                CauseEventId: "evt-2",
                Summary: "Scheduler assigned T1 (Attempt A1) to Worker 1",
                PlainExplanation: "The Scheduler checked the Worker Registry, saw Worker 1 was healthy and free, and tagged this run as Attempt A1.",
                Payload: new Dictionary<string, string> { ["TaskId"] = "T1", ["AttemptId"] = "A1", ["WorkerId"] = "worker-1" }),
            new(
                Id: "evt-4",
                TimestampMs: 100,
                Sequence: 4,
                Type: SimulationEventType.HeartbeatReceived,
                Category: EventCategory.Heartbeats,
                SourceNodeId: "worker-1",
                TargetNodeId: "coordinator",
                ModuleId: "coord-hb-monitor",
                CauseEventId: null,
                Summary: "Coordinator received Heartbeat from Worker 1",
                PlainExplanation: "Worker 1 sent an 'I am alive' signal, resetting the Coordinator's timeout timer for Worker 1.",
                Payload: new Dictionary<string, string> { ["WorkerId"] = "worker-1", ["Status"] = "Healthy" }),
            new(
                Id: "evt-5",
                TimestampMs: 130,
                Sequence: 5,
                Type: SimulationEventType.TaskStarted,
                Category: EventCategory.Jobs,
                SourceNodeId: "worker-1",
                TargetNodeId: null,
                ModuleId: "worker-1-executor",
                CauseEventId: "evt-3",
                Summary: "Worker 1 started running T1 (Attempt A1)",
                PlainExplanation: "Task T1 arrived at Worker 1's Inbox and moved into the Task Executor to begin simulated work.",
                Payload: new Dictionary<string, string> { ["TaskId"] = "T1", ["AttemptId"] = "A1" })
        };

        var metrics = new SimulatedMetrics(
            SimulatedTimeMs: 130,
            QueueDepth: 1,
            PendingTasks: 1,
            RunningTasks: 1,
            CompletedTasks: 0,
            DispatchedTasks: 1,
            RetryCount: 0,
            MessagesSent: 3,
            MessagesDropped: 0);

        return new SimulationState(
            ScenarioId: config.ScenarioId,
            ScenarioTitle: "Distributed Task Scheduling",
            IsPrototypeStub: true,
            Seed: seed,
            SimulatedTimeMs: 130,
            Nodes: nodes,
            Links: links,
            InFlightMessages: messages,
            Jobs: jobs,
            Tasks: tasks,
            Attempts: attempts,
            EventLog: events,
            Metrics: metrics);
    }

    private static NodeState BuildWorkerNode(
        string id,
        string name,
        NodeHealthStatus health,
        double gridX,
        double gridY,
        string? activeTask,
        string statusSummary,
        long lastHbMs)
    {
        var isBusy = activeTask is not null;
        var modules = new List<ModuleState>
        {
            new(
                Id: $"{id}-inbox",
                NodeId: id,
                Name: "Inbox",
                ShortRole: "Receives task dispatches",
                PlainDescription: "Receives task assignment messages coming from the Coordinator and forwards them to the Task Executor.",
                Status: isBusy ? ModuleStatus.Healthy : ModuleStatus.Idle,
                GridX: 0.0,
                GridY: 0.0,
                QueueItems: isBusy ? [$"Received {activeTask}"] : [],
                Counters: new Dictionary<string, string>
                {
                    ["Messages Received (Simulated)"] = isBusy ? "1" : "0"
                }),
            new(
                Id: $"{id}-executor",
                NodeId: id,
                Name: "Task Executor",
                ShortRole: "Runs assigned tasks",
                PlainDescription: "Performs the simulated work for one task at a time and creates a completion result when finished.",
                Status: isBusy ? ModuleStatus.Processing : ModuleStatus.Idle,
                GridX: 2.9,
                GridY: 0.0,
                QueueItems: isBusy ? [$"Running {activeTask}"] : ["Idle (Ready)"],
                Counters: new Dictionary<string, string>
                {
                    ["Active Task"] = activeTask ?? "None",
                    ["Completed (Simulated)"] = "0"
                },
                ActiveHint: isBusy ? $"Running {activeTask}" : null),
            new(
                Id: $"{id}-outbox",
                NodeId: id,
                Name: "Result Outbox",
                ShortRole: "Sends finished results",
                PlainDescription: "Sends the completed task result — stamped with its Attempt ID — back to the Coordinator.",
                Status: ModuleStatus.Idle,
                GridX: 5.8,
                GridY: 0.0,
                QueueItems: [],
                Counters: new Dictionary<string, string>
                {
                    ["Results Sent (Simulated)"] = "0"
                }),
            new(
                Id: $"{id}-local-state",
                NodeId: id,
                Name: "Local Task State",
                ShortRole: "Tracks local progress",
                PlainDescription: "Keeps a small local record of which task attempt is running here. Lost if the worker crashes.",
                Status: isBusy ? ModuleStatus.Processing : ModuleStatus.Idle,
                GridX: 2.9,
                GridY: 3.0,
                QueueItems: isBusy ? [$"State: Running {activeTask}"] : ["State: Empty"],
                Counters: new Dictionary<string, string>
                {
                    ["Used Capacity"] = isBusy ? "1 / 1" : "0 / 1"
                }),
            new(
                Id: $"{id}-heartbeat",
                NodeId: id,
                Name: "Heartbeat Sender",
                ShortRole: "Sends 'I am alive' pings",
                PlainDescription: "Periodically sends a lightweight heartbeat message to the Coordinator. If this worker crashes, heartbeats stop.",
                Status: ModuleStatus.Healthy,
                GridX: 5.8,
                GridY: 3.0,
                QueueItems: [$"Last ping @ {lastHbMs} ms"],
                Counters: new Dictionary<string, string>
                {
                    ["Heartbeats Sent (Simulated)"] = "1"
                })
        };

        var links = new List<ModuleLink>
        {
            new($"{id}-ml-1", $"{id}-inbox", $"{id}-executor", "Start Task"),
            new($"{id}-ml-2", $"{id}-executor", $"{id}-outbox", "Result"),
            new($"{id}-ml-3", $"{id}-executor", $"{id}-local-state", "Update State", IsDashed: true),
            new($"{id}-ml-4", $"{id}-heartbeat", $"{id}-outbox", "Ping", IsDashed: true)
        };

        return new NodeState(
            Id: id,
            Name: name,
            Role: NodeRole.Worker,
            Health: health,
            Capacity: 1,
            ActiveTaskCount: isBusy ? 1 : 0,
            StatusSummary: statusSummary,
            PlainDescription: "A worker machine that executes tasks sent by the Coordinator and sends regular heartbeats to prove it is still alive.",
            GridX: gridX,
            GridY: gridY,
            Modules: modules,
            InternalLinks: links,
            InboxItems: isBusy ? [$"Dispatch {activeTask}"] : [],
            OutboxItems: [$"Heartbeat @ {lastHbMs} ms"]);
    }
}
