using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Engine;

/// <summary>
/// Pure C# deterministic discrete-event simulation engine for Distributed Task Scheduling.
/// Orders every event strictly by (SimulatedTimeMs, SequenceNumber) and never references UI or wall-clock time.
/// </summary>
public sealed class DiscreteEventSimulationEngine : ISimulationEngine
{
    private readonly ScenarioConfig _config;
    private readonly int _seed;

    private Random _rng = null!;
    private PriorityQueue<ScheduledWork, (long TimeMs, long QueueSeq)> _queue = null!;
    private long _currentTimeMs;
    private long _nextQueueSeq;
    private long _nextEventSeq;
    private int _nextJobNum;
    private int _nextTaskNum;
    private int _nextAttemptNum;
    private int _nextMessageNum;

    // Armed user fault injections (applied deterministically to the next non-heartbeat network message)
    private readonly Queue<int> _armedDelayMs = new();
    private int _armedDropCount;

    // Mutable internal simulation entities (never leaked outside the engine)
    private ClientEntity _client = null!;
    private CoordinatorEntity _coordinator = null!;
    private readonly Dictionary<string, WorkerEntity> _workers = new();
    private readonly List<string> _workerOrder = [];
    private readonly Dictionary<string, JobEntity> _jobs = new();
    private readonly Dictionary<string, TaskEntity> _tasks = new();
    private readonly Dictionary<string, AttemptEntity> _attempts = new();
    private readonly Dictionary<string, MessageEntity> _inFlightMessages = new();
    private readonly List<EventRecord> _eventLog = [];

    // Metrics accumulators
    private int _dispatchedCount;
    private int _retryCount;
    private int _messagesSentCount;
    private int _messagesDroppedCount;
    private int _staleResultsIgnoredCount;
    private long? _lastJobDurationMs;
    private string? _lastActiveModuleId;
    private bool _monitorSweepScheduled;

    private SimulationState _snapshot = null!;

    public DiscreteEventSimulationEngine(ScenarioConfig? config = null, int seed = 42)
    {
        _config = config ?? new ScenarioConfig();
        _seed = seed;
        InitializeInternalState();
    }

    public bool IsPrototypeStub => false;

    public event EventHandler<SimulationState>? StateChanged;

    public SimulationState GetState() => _snapshot;

    public long? PeekNextEventTimeMs()
    {
        while (_queue.TryPeek(out var next, out _))
        {
            if (IsWorkStillValid(next))
            {
                return next.ScheduledTimeMs;
            }

            _queue.Dequeue();
        }

        return null;
    }

    public SimulationState Reset()
    {
        InitializeInternalState();
        StateChanged?.Invoke(this, _snapshot);
        return _snapshot;
    }

    public void DispatchAction(UserAction action)
    {
        switch (action)
        {
            case SubmitJobAction submit:
                EnqueueWork(_currentTimeMs, new SubmitJobWork(submit.JobLabel, Math.Max(1, submit.TaskCount)));
                break;

            case CrashWorkerAction crash:
                if (_workers.ContainsKey(crash.WorkerId))
                {
                    EnqueueWork(_currentTimeMs, new CrashWorkerWork(crash.WorkerId));
                }
                break;

            case RecoverWorkerAction recover:
                if (_workers.ContainsKey(recover.WorkerId))
                {
                    EnqueueWork(_currentTimeMs, new RecoverWorkerWork(recover.WorkerId));
                }
                break;

            case DelayNextMessageAction delay:
                _armedDelayMs.Enqueue(Math.Max(200, delay.ExtraDelayMs));
                RebuildSnapshot();
                StateChanged?.Invoke(this, _snapshot);
                break;

            case DropNextMessageAction:
                _armedDropCount++;
                RebuildSnapshot();
                StateChanged?.Invoke(this, _snapshot);
                break;
        }
    }

    public StepResult Step()
    {
        while (_queue.TryDequeue(out var work, out _))
        {
            if (!IsWorkStillValid(work))
            {
                continue;
            }

            _currentTimeMs = Math.Max(_currentTimeMs, work.ScheduledTimeMs);
            var loggedEvent = ExecuteWork(work);
            if (loggedEvent is null)
            {
                // Internal maintenance check (e.g. heartbeat sweep when all workers are healthy)
                // Keep stepping until an observable event executes or queue empties.
                continue;
            }

            RebuildSnapshot();
            StateChanged?.Invoke(this, _snapshot);
            return new StepResult(true, loggedEvent, _snapshot);
        }

        return new StepResult(false, null, _snapshot, "No queued events remaining. Click '+ Submit Job' to start new work.");
    }

    private void InitializeInternalState()
    {
        _rng = new Random(_seed);
        _queue = new PriorityQueue<ScheduledWork, (long TimeMs, long QueueSeq)>();
        _currentTimeMs = 0;
        _nextQueueSeq = 0;
        _nextEventSeq = 0;
        _nextJobNum = 0;
        _nextTaskNum = 0;
        _nextAttemptNum = 0;
        _nextMessageNum = 0;

        _armedDelayMs.Clear();
        _armedDropCount = 0;

        _workers.Clear();
        _workerOrder.Clear();
        _jobs.Clear();
        _tasks.Clear();
        _attempts.Clear();
        _inFlightMessages.Clear();
        _eventLog.Clear();

        _dispatchedCount = 0;
        _retryCount = 0;
        _messagesSentCount = 0;
        _messagesDroppedCount = 0;
        _staleResultsIgnoredCount = 0;
        _lastJobDurationMs = null;
        _lastActiveModuleId = null;
        _monitorSweepScheduled = false;

        _client = new ClientEntity();
        _coordinator = new CoordinatorEntity();

        var workerCount = Math.Max(1, _config.WorkerCount);
        var workerCoords = new (double X, double Y)[]
        {
            (3.1, -1.6),
            (4.1, 0.7),
            (3.4, 3.3)
        };

        for (var i = 0; i < workerCount; i++)
        {
            var id = $"worker-{i + 1}";
            var name = $"Worker {i + 1}";
            (double X, double Y) coord = i < workerCoords.Length
                ? workerCoords[i]
                : (4.2 + (i - 2) * 0.6, 3.3 + (i - 2) * 1.4);

            var worker = new WorkerEntity(id, name, coord.X, coord.Y);
            _workers[id] = worker;
            _workerOrder.Add(id);

            _coordinator.WorkerRegistry[id] = new WorkerRegistryEntry(
                id,
                name,
                NodeHealthStatus.Healthy,
                lastHeartbeatMs: 0,
                assignedTaskId: null,
                assignedAttemptId: null);

            // Stagger initial heartbeats cleanly so workers don't collide on the exact same tick
            var firstHbMs = 180 + i * 110;
            EnqueueWork(firstHbMs, new EmitHeartbeatWork(id, worker.BootGeneration));
        }

        // Periodic coordinator heartbeat monitor
        ScheduleMonitorSweepIfNeeded(250);

        if (_config.AutoSubmitInitialJob)
        {
            EnqueueWork(0, new SubmitJobWork("Initial Job", taskCount: 1));
        }

        RebuildSnapshot();
    }

    private void EnqueueWork(long scheduledTimeMs, ScheduledWork work)
    {
        var seq = ++_nextQueueSeq;
        work.ScheduledTimeMs = scheduledTimeMs;
        _queue.Enqueue(work, (scheduledTimeMs, seq));
    }

    private bool IsWorkStillValid(ScheduledWork work)
    {
        return work switch
        {
            EmitHeartbeatWork hb => _workers.TryGetValue(hb.WorkerId, out var w) && !w.IsCrashed && w.BootGeneration == hb.BootGeneration,
            CompleteTaskWork comp => _workers.TryGetValue(comp.WorkerId, out var w) && !w.IsCrashed && w.ActiveAttemptId == comp.AttemptId,
            AttemptTimeoutWork timeout => _tasks.TryGetValue(timeout.TaskId, out var t) &&
                                          t.AcceptedAttemptId is null &&
                                          t.CurrentAttemptId == timeout.AttemptId &&
                                          _attempts.TryGetValue(timeout.AttemptId, out var a) &&
                                          a.Status is AttemptStatus.Dispatched or AttemptStatus.Running,
            _ => true
        };
    }

    private EventRecord? ExecuteWork(ScheduledWork work)
    {
        return work switch
        {
            SubmitJobWork submit => HandleSubmitJob(submit),
            DeliverMessageWork deliver => HandleDeliverMessage(deliver),
            EnqueueTasksWork enqueue => HandleEnqueueTasks(enqueue),
            ScheduleNextWork sched => HandleScheduleNext(sched),
            CompleteTaskWork comp => HandleCompleteTask(comp),
            CrashWorkerWork crash => HandleCrashWorker(crash),
            RecoverWorkerWork rec => HandleRecoverWorker(rec),
            EmitHeartbeatWork hb => HandleEmitHeartbeat(hb),
            MonitorSweepWork => HandleMonitorSweep(),
            RequeueTaskWork requeue => HandleRequeueTask(requeue),
            FinishJobWork finishJob => HandleFinishJob(finishJob),
            AttemptTimeoutWork timeout => HandleAttemptTimeout(timeout),
            _ => null
        };
    }

    private EventRecord HandleSubmitJob(SubmitJobWork work)
    {
        var jobId = $"J{++_nextJobNum}";
        var taskIds = new List<string>(work.TaskCount);
        for (var i = 0; i < work.TaskCount; i++)
        {
            var taskId = $"T{++_nextTaskNum}";
            taskIds.Add(taskId);
            _tasks[taskId] = new TaskEntity(taskId, jobId);
        }

        var job = new JobEntity(jobId, taskIds, _currentTimeMs);
        _jobs[jobId] = job;
        _client.JobsSubmitted++;
        _client.OpenJobs.Add(jobId);

        var evt = RecordEvent(
            SimulationEventType.JobSubmitted,
            EventCategory.Jobs,
            sourceNodeId: "client",
            targetNodeId: "coordinator",
            moduleId: "client-submitter",
            causeEventId: null,
            summary: $"Client submitted Job {jobId} ({taskIds.Count} {(taskIds.Count == 1 ? "task" : "tasks")})",
            plainExplanation: $"The Client created Job {jobId} containing {string.Join(", ", taskIds)} and sent it over the simulated network to the Coordinator.",
            payload: new Dictionary<string, string>
            {
                ["JobId"] = jobId,
                ["Tasks"] = string.Join(", ", taskIds)
            });

        SendNetworkMessage(
            MessageKind.JobSubmit,
            sourceNodeId: "client",
            targetNodeId: "coordinator",
            jobId: jobId,
            taskId: taskIds.FirstOrDefault(),
            attemptId: null,
            generation: 0,
            pillLabel: $"Submit {jobId}",
            causeEventId: evt.Id);

        return evt;
    }

    private EventRecord? HandleDeliverMessage(DeliverMessageWork work)
    {
        if (!_inFlightMessages.Remove(work.MessageId, out var msg))
        {
            return null;
        }

        // Check if message was armed to drop on delivery (or destination worker is crashed)
        if (work.WillDrop)
        {
            msg.Status = MessageDeliveryStatus.Dropped;
            _messagesDroppedCount++;
            return RecordEvent(
                SimulationEventType.MessageDropped,
                EventCategory.Faults,
                sourceNodeId: msg.SourceNodeId,
                targetNodeId: msg.TargetNodeId,
                moduleId: null,
                causeEventId: msg.CauseEventId,
                summary: $"Network dropped {msg.Kind} ({msg.PillLabel})",
                plainExplanation: $"The simulated network dropped message {msg.Id} ({msg.PillLabel}) before it could reach {FormatNodeName(msg.TargetNodeId)}. The system must rely on timeouts and retries to recover.",
                payload: new Dictionary<string, string>
                {
                    ["MessageId"] = msg.Id,
                    ["Kind"] = msg.Kind.ToString(),
                    ["TaskId"] = msg.TaskId ?? "-",
                    ["AttemptId"] = msg.AttemptId ?? "-"
                });
        }

        msg.Status = MessageDeliveryStatus.Delivered;

        return msg.Kind switch
        {
            MessageKind.JobSubmit => HandleJobReceivedAtCoordinator(msg),
            MessageKind.TaskDispatch => HandleTaskArrivedAtWorker(msg),
            MessageKind.TaskResult => HandleResultArrivedAtCoordinator(msg),
            MessageKind.Heartbeat => HandleHeartbeatArrivedAtCoordinator(msg),
            MessageKind.ClientResponse => HandleClientResponseArrived(msg),
            _ => null
        };
    }

    private EventRecord HandleJobReceivedAtCoordinator(MessageEntity msg)
    {
        var job = _jobs[msg.JobId!];
        _coordinator.JobsAccepted++;
        _coordinator.RecentInbox.Enqueue($"Job {job.Id}");
        TrimBuffer(_coordinator.RecentInbox);

        var evt = RecordEvent(
            SimulationEventType.JobReceived,
            EventCategory.Jobs,
            sourceNodeId: "coordinator",
            targetNodeId: null,
            moduleId: "coord-req-handler",
            causeEventId: msg.CauseEventId,
            summary: $"Coordinator received Job {job.Id}",
            plainExplanation: $"The Coordinator's Request Handler accepted Job {job.Id} from the Client and prepared {job.TaskIds.Count} {(job.TaskIds.Count == 1 ? "task" : "tasks")} for queuing.",
            payload: new Dictionary<string, string>
            {
                ["JobId"] = job.Id,
                ["Tasks"] = string.Join(", ", job.TaskIds)
            });

        EnqueueWork(_currentTimeMs + 25, new EnqueueTasksWork(job.Id, evt.Id));
        return evt;
    }

    private EventRecord HandleEnqueueTasks(EnqueueTasksWork work)
    {
        var job = _jobs[work.JobId];
        foreach (var taskId in job.TaskIds)
        {
            var task = _tasks[taskId];
            task.Status = SimulationTaskStatus.Queued;
            _coordinator.TaskQueue.Enqueue(taskId);
            _coordinator.TotalEnqueued++;
        }

        var evt = RecordEvent(
            SimulationEventType.TaskEnqueued,
            EventCategory.Jobs,
            sourceNodeId: "coordinator",
            targetNodeId: null,
            moduleId: "coord-job-queue",
            causeEventId: work.CauseEventId,
            summary: $"Queued {string.Join(", ", job.TaskIds)} in Coordinator Job Queue",
            plainExplanation: $"Tasks {string.Join(", ", job.TaskIds)} entered the Coordinator's waiting queue. The Scheduler will pick healthy workers with free capacity.",
            payload: new Dictionary<string, string>
            {
                ["JobId"] = job.Id,
                ["QueueDepth"] = _coordinator.TaskQueue.Count.ToString()
            });

        EnqueueWork(_currentTimeMs + 25, new ScheduleNextWork(evt.Id));
        return evt;
    }

    private EventRecord? HandleScheduleNext(ScheduleNextWork work)
    {
        if (_coordinator.TaskQueue.Count == 0)
        {
            return null;
        }

        var chosenEntry = PickHealthyFreeWorker();
        if (chosenEntry is null)
        {
            return null;
        }

        var taskId = _coordinator.TaskQueue.Dequeue();
        var task = _tasks[taskId];
        if (task.AcceptedAttemptId is not null)
        {
            // Already completed; try next queued task if any
            if (_coordinator.TaskQueue.Count > 0)
            {
                EnqueueWork(_currentTimeMs, new ScheduleNextWork(work.CauseEventId));
            }
            return null;
        }

        var generation = ++task.NextGeneration;
        var attemptId = $"A{++_nextAttemptNum}";
        var attempt = new AttemptEntity(attemptId, taskId, chosenEntry.WorkerId, generation, AttemptStatus.Dispatched, _currentTimeMs);
        _attempts[attemptId] = attempt;

        task.Status = SimulationTaskStatus.Dispatched;
        task.AssignedWorkerId = chosenEntry.WorkerId;
        task.CurrentAttemptId = attemptId;

        chosenEntry.AssignedTaskId = taskId;
        chosenEntry.AssignedAttemptId = attemptId;

        _dispatchedCount++;
        _coordinator.RecentOutbox.Enqueue($"Dispatch {taskId} ({attemptId}) → {chosenEntry.WorkerName}");
        TrimBuffer(_coordinator.RecentOutbox);

        var evt = RecordEvent(
            SimulationEventType.TaskDispatched,
            EventCategory.Network,
            sourceNodeId: "coordinator",
            targetNodeId: chosenEntry.WorkerId,
            moduleId: "coord-scheduler",
            causeEventId: work.CauseEventId,
            summary: $"Scheduler dispatched {taskId} (Attempt {attemptId}, Gen {generation}) → {chosenEntry.WorkerName}",
            plainExplanation: $"The Scheduler checked the Worker Registry, chose healthy idle {chosenEntry.WorkerName}, stamped the assignment with Attempt {attemptId} (generation {generation}), and sent it across the network.",
            payload: new Dictionary<string, string>
            {
                ["TaskId"] = taskId,
                ["AttemptId"] = attemptId,
                ["Generation"] = generation.ToString(),
                ["WorkerId"] = chosenEntry.WorkerId
            });

        SendNetworkMessage(
            MessageKind.TaskDispatch,
            sourceNodeId: "coordinator",
            targetNodeId: chosenEntry.WorkerId,
            jobId: task.JobId,
            taskId: taskId,
            attemptId: attemptId,
            generation: generation,
            pillLabel: $"Dispatch {taskId} · {attemptId}",
            causeEventId: evt.Id);

        // Schedule Coordinator-side attempt timeout so dropped or severely delayed messages still recover cleanly
        EnqueueWork(_currentTimeMs + _config.AttemptTimeoutMs, new AttemptTimeoutWork(taskId, attemptId, evt.Id));

        // If more queued tasks and more free workers remain, schedule next assignment right after
        if (_coordinator.TaskQueue.Count > 0 && PickHealthyFreeWorker() is not null)
        {
            EnqueueWork(_currentTimeMs + 20, new ScheduleNextWork(evt.Id));
        }

        return evt;
    }

    private EventRecord HandleTaskArrivedAtWorker(MessageEntity msg)
    {
        var worker = _workers[msg.TargetNodeId];
        if (worker.IsCrashed)
        {
            _messagesDroppedCount++;
            return RecordEvent(
                SimulationEventType.MessageDropped,
                EventCategory.Faults,
                sourceNodeId: msg.SourceNodeId,
                targetNodeId: worker.Id,
                moduleId: $"{worker.Id}-inbox",
                causeEventId: msg.CauseEventId,
                summary: $"{worker.Name} is crashed and could not receive {msg.TaskId} ({msg.AttemptId})",
                plainExplanation: $"The dispatch arrived while {worker.Name} was offline. A crashed worker cannot accept or run tasks until it recovers.",
                payload: new Dictionary<string, string>
                {
                    ["WorkerId"] = worker.Id,
                    ["TaskId"] = msg.TaskId ?? "-",
                    ["AttemptId"] = msg.AttemptId ?? "-"
                });
        }

        var task = _tasks[msg.TaskId!];
        var attempt = _attempts[msg.AttemptId!];

        worker.MessagesReceived++;
        worker.ActiveTaskId = task.Id;
        worker.ActiveAttemptId = attempt.Id;
        worker.BusySinceMs = _currentTimeMs;
        worker.Health = NodeHealthStatus.Processing;
        worker.RecentInbox.Enqueue($"Dispatch {task.Id} ({attempt.Id})");
        TrimBuffer(worker.RecentInbox);

        if (attempt.Status == AttemptStatus.Dispatched)
        {
            attempt.Status = AttemptStatus.Running;
        }

        if (task.CurrentAttemptId == attempt.Id && task.AcceptedAttemptId is null)
        {
            task.Status = SimulationTaskStatus.Running;
        }

        var jitter = _rng.Next(-60, 81);
        var execDuration = Math.Max(200, _config.TaskExecutionDurationMs + jitter);
        EnqueueWork(_currentTimeMs + execDuration, new CompleteTaskWork(worker.Id, task.Id, attempt.Id, attempt.Generation, msg.CauseEventId));

        return RecordEvent(
            SimulationEventType.TaskStarted,
            EventCategory.Jobs,
            sourceNodeId: worker.Id,
            targetNodeId: null,
            moduleId: $"{worker.Id}-executor",
            causeEventId: msg.CauseEventId,
            summary: $"{worker.Name} started running {task.Id} (Attempt {attempt.Id})",
            plainExplanation: $"{worker.Name}'s Inbox handed {task.Id} (Attempt {attempt.Id}) to the Task Executor. Simulated work will finish in {execDuration} ms.",
            payload: new Dictionary<string, string>
            {
                ["WorkerId"] = worker.Id,
                ["TaskId"] = task.Id,
                ["AttemptId"] = attempt.Id,
                ["SimulatedDurationMs"] = execDuration.ToString()
            });
    }

    private EventRecord HandleCompleteTask(CompleteTaskWork work)
    {
        var worker = _workers[work.WorkerId];
        if (worker.BusySinceMs.HasValue)
        {
            worker.TotalBusyMs += _currentTimeMs - worker.BusySinceMs.Value;
            worker.BusySinceMs = null;
        }

        worker.ActiveTaskId = null;
        worker.ActiveAttemptId = null;
        worker.TasksCompleted++;
        worker.ResultsSent++;
        worker.Health = NodeHealthStatus.Healthy;
        worker.RecentOutbox.Enqueue($"Result {work.TaskId} ({work.AttemptId})");
        TrimBuffer(worker.RecentOutbox);

        var evt = RecordEvent(
            SimulationEventType.TaskExecutionFinished,
            EventCategory.Jobs,
            sourceNodeId: worker.Id,
            targetNodeId: "coordinator",
            moduleId: $"{worker.Id}-outbox",
            causeEventId: work.CauseEventId,
            summary: $"{worker.Name} finished {work.TaskId} (Attempt {work.AttemptId}) and sent result",
            plainExplanation: $"{worker.Name} completed simulated execution of {work.TaskId} and put a completion message tagged with Attempt {work.AttemptId} (Gen {work.Generation}) onto the network.",
            payload: new Dictionary<string, string>
            {
                ["WorkerId"] = worker.Id,
                ["TaskId"] = work.TaskId,
                ["AttemptId"] = work.AttemptId,
                ["Generation"] = work.Generation.ToString()
            });

        SendNetworkMessage(
            MessageKind.TaskResult,
            sourceNodeId: worker.Id,
            targetNodeId: "coordinator",
            jobId: _tasks[work.TaskId].JobId,
            taskId: work.TaskId,
            attemptId: work.AttemptId,
            generation: work.Generation,
            pillLabel: $"Result {work.TaskId} · {work.AttemptId}",
            causeEventId: evt.Id);

        return evt;
    }

    private EventRecord HandleResultArrivedAtCoordinator(MessageEntity msg)
    {
        var task = _tasks[msg.TaskId!];
        var attempt = _attempts[msg.AttemptId!];
        var workerId = msg.SourceNodeId;

        _coordinator.RecentInbox.Enqueue($"Result {task.Id} ({attempt.Id})");
        TrimBuffer(_coordinator.RecentInbox);

        if (_coordinator.WorkerRegistry.TryGetValue(workerId, out var reg) && reg.AssignedAttemptId == attempt.Id)
        {
            reg.AssignedTaskId = null;
            reg.AssignedAttemptId = null;
        }

        // Core Invariant 1 & 2: Reject any stale or outdated attempt result!
        var isStale = task.AcceptedAttemptId is not null ||
                      task.CurrentAttemptId != attempt.Id ||
                      attempt.Status == AttemptStatus.Lost;

        if (isStale)
        {
            attempt.Status = AttemptStatus.StaleIgnored;
            _staleResultsIgnoredCount++;

            var winnerText = task.AcceptedAttemptId is not null
                ? $"already accepted {task.AcceptedAttemptId}"
                : $"already moved on to {task.CurrentAttemptId ?? "requeued state"}";

            return RecordEvent(
                SimulationEventType.StaleResultIgnored,
                EventCategory.Faults,
                sourceNodeId: "coordinator",
                targetNodeId: workerId,
                moduleId: "coord-result-handler",
                causeEventId: msg.CauseEventId,
                summary: $"Ignored stale result for {task.Id} from old Attempt {attempt.Id} (Gen {attempt.Generation})",
                plainExplanation: $"A late completion for {task.Id} arrived from {FormatNodeName(workerId)} (Attempt {attempt.Id}, Gen {attempt.Generation}), but the Coordinator {winnerText}. Stamping attempts with generation IDs prevents old replies from overwriting newer results.",
                payload: new Dictionary<string, string>
                {
                    ["TaskId"] = task.Id,
                    ["IgnoredAttemptId"] = attempt.Id,
                    ["IgnoredGeneration"] = attempt.Generation.ToString(),
                    ["AuthoritativeAttemptId"] = task.AcceptedAttemptId ?? task.CurrentAttemptId ?? "None"
                });
        }

        // Accept authoritative final result
        attempt.Status = AttemptStatus.Completed;
        task.Status = SimulationTaskStatus.Completed;
        task.AcceptedAttemptId = attempt.Id;
        _coordinator.AcceptedResults++;

        var evt = RecordEvent(
            SimulationEventType.ResultReceived,
            EventCategory.Jobs,
            sourceNodeId: "coordinator",
            targetNodeId: null,
            moduleId: "coord-result-handler",
            causeEventId: msg.CauseEventId,
            summary: $"Coordinator accepted result for {task.Id} (Attempt {attempt.Id})",
            plainExplanation: $"The Result Handler verified Attempt {attempt.Id} (Gen {attempt.Generation}) matches the active attempt and recorded {task.Id} as permanently completed.",
            payload: new Dictionary<string, string>
            {
                ["TaskId"] = task.Id,
                ["AcceptedAttemptId"] = attempt.Id,
                ["WorkerId"] = workerId
            });

        // Check if parent job is now completely done -> schedule JobFinished as its own discrete step
        var job = _jobs[task.JobId];
        if (job.TaskIds.All(tid => _tasks[tid].AcceptedAttemptId is not null) && job.Status != SimulationTaskStatus.Completed)
        {
            EnqueueWork(_currentTimeMs + 10, new FinishJobWork(job.Id, evt.Id));
        }

        // Dispatch any waiting task to the newly freed worker
        if (_coordinator.TaskQueue.Count > 0)
        {
            EnqueueWork(_currentTimeMs + 20, new ScheduleNextWork(evt.Id));
        }

        return evt;
    }

    private EventRecord? HandleFinishJob(FinishJobWork work)
    {
        var job = _jobs[work.JobId];
        if (job.Status == SimulationTaskStatus.Completed)
        {
            return null;
        }

        job.Status = SimulationTaskStatus.Completed;
        job.CompletedAtMs = _currentTimeMs;
        _lastJobDurationMs = _currentTimeMs - job.SubmittedAtMs;

        _coordinator.RecentOutbox.Enqueue($"Reply {job.Id} → Client");
        TrimBuffer(_coordinator.RecentOutbox);

        var evt = RecordEvent(
            SimulationEventType.JobFinished,
            EventCategory.Jobs,
            sourceNodeId: "coordinator",
            targetNodeId: "client",
            moduleId: "coord-outbox",
            causeEventId: work.CauseEventId,
            summary: $"Job {job.Id} finished in {_lastJobDurationMs} ms (Simulated)",
            plainExplanation: $"All tasks in Job {job.Id} now have an accepted final result. Coordinator is sending the final completion reply back to the Client.",
            payload: new Dictionary<string, string>
            {
                ["JobId"] = job.Id,
                ["SimulatedDurationMs"] = _lastJobDurationMs.Value.ToString()
            });

        SendNetworkMessage(
            MessageKind.ClientResponse,
            sourceNodeId: "coordinator",
            targetNodeId: "client",
            jobId: job.Id,
            taskId: null,
            attemptId: null,
            generation: 0,
            pillLabel: $"Done {job.Id}",
            causeEventId: evt.Id);

        return evt;
    }

    private EventRecord HandleClientResponseArrived(MessageEntity msg)
    {
        var jobId = msg.JobId!;
        _client.RepliesReceived++;
        _client.OpenJobs.Remove(jobId);
        _client.CompletedJobs.Add(jobId);

        return RecordEvent(
            SimulationEventType.ClientResponded,
            EventCategory.Jobs,
            sourceNodeId: "client",
            targetNodeId: null,
            moduleId: "client-tracker",
            causeEventId: msg.CauseEventId,
            summary: $"Client received completion reply for Job {jobId}",
            plainExplanation: $"The Client's Response Inbox delivered the final result for Job {jobId} to the Job Tracker, completing the end-to-end request lifecycle.",
            payload: new Dictionary<string, string>
            {
                ["JobId"] = jobId,
                ["TotalCompletedJobs"] = _client.CompletedJobs.Count.ToString()
            });
    }

    private EventRecord? HandleEmitHeartbeat(EmitHeartbeatWork work)
    {
        var worker = _workers[work.WorkerId];
        if (worker.IsCrashed || worker.BootGeneration != work.BootGeneration)
        {
            return null;
        }

        worker.HeartbeatsSent++;
        worker.LastHeartbeatSentMs = _currentTimeMs;
        worker.RecentOutbox.Enqueue($"Heartbeat @ {_currentTimeMs} ms");
        TrimBuffer(worker.RecentOutbox);

        // Schedule next periodic heartbeat
        EnqueueWork(_currentTimeMs + _config.HeartbeatIntervalMs, new EmitHeartbeatWork(worker.Id, worker.BootGeneration));

        var evt = RecordEvent(
            SimulationEventType.HeartbeatSent,
            EventCategory.Heartbeats,
            sourceNodeId: worker.Id,
            targetNodeId: "coordinator",
            moduleId: $"{worker.Id}-heartbeat",
            causeEventId: null,
            summary: $"{worker.Name} sent Heartbeat ping",
            plainExplanation: $"{worker.Name} periodically tells the Coordinator 'I am still alive'. If these pings stop arriving, the Coordinator will eventually declare a timeout.",
            payload: new Dictionary<string, string>
            {
                ["WorkerId"] = worker.Id,
                ["SentAtMs"] = _currentTimeMs.ToString()
            });

        SendNetworkMessage(
            MessageKind.Heartbeat,
            sourceNodeId: worker.Id,
            targetNodeId: "coordinator",
            jobId: null,
            taskId: null,
            attemptId: null,
            generation: 0,
            pillLabel: $"HB · {ShortWorkerTag(worker.Id)}",
            causeEventId: evt.Id);

        return evt;
    }

    private EventRecord HandleHeartbeatArrivedAtCoordinator(MessageEntity msg)
    {
        var workerId = msg.SourceNodeId;
        var worker = _workers[workerId];
        var reg = _coordinator.WorkerRegistry[workerId];
        var wasNotHealthy = reg.Health != NodeHealthStatus.Healthy;

        reg.LastHeartbeatMs = _currentTimeMs;
        reg.Health = NodeHealthStatus.Healthy;
        ScheduleMonitorSweepIfNeeded(_currentTimeMs + 250);

        if (!worker.IsCrashed && worker.Health == NodeHealthStatus.Recovering)
        {
            worker.Health = worker.ActiveTaskId is not null ? NodeHealthStatus.Processing : NodeHealthStatus.Healthy;
        }

        var evt = RecordEvent(
            SimulationEventType.HeartbeatReceived,
            EventCategory.Heartbeats,
            sourceNodeId: "coordinator",
            targetNodeId: workerId,
            moduleId: "coord-hb-monitor",
            causeEventId: msg.CauseEventId,
            summary: wasNotHealthy
                ? $"Coordinator received Heartbeat from {worker.Name} (marked Healthy again)"
                : $"Coordinator received Heartbeat from {worker.Name}",
            plainExplanation: $"Heartbeat Monitor refreshed {worker.Name}'s last-seen timestamp in the Worker Registry to {_currentTimeMs} ms.",
            payload: new Dictionary<string, string>
            {
                ["WorkerId"] = workerId,
                ["LastHeartbeatMs"] = _currentTimeMs.ToString(),
                ["RegistryHealth"] = reg.Health.ToString()
            });

        // If this worker just recovered and tasks are waiting, assign work immediately
        if (wasNotHealthy && _coordinator.TaskQueue.Count > 0)
        {
            EnqueueWork(_currentTimeMs + 20, new ScheduleNextWork(evt.Id));
        }

        return evt;
    }

    private EventRecord? HandleCrashWorker(CrashWorkerWork work)
    {
        var worker = _workers[work.WorkerId];
        if (worker.IsCrashed)
        {
            return null;
        }

        if (worker.BusySinceMs.HasValue)
        {
            worker.TotalBusyMs += _currentTimeMs - worker.BusySinceMs.Value;
            worker.BusySinceMs = null;
        }

        var lostTask = worker.ActiveTaskId;
        var lostAttempt = worker.ActiveAttemptId;

        worker.IsCrashed = true;
        worker.Health = NodeHealthStatus.Failed;
        worker.ActiveTaskId = null;
        worker.ActiveAttemptId = null;

        return RecordEvent(
            SimulationEventType.WorkerCrashed,
            EventCategory.Faults,
            sourceNodeId: worker.Id,
            targetNodeId: null,
            moduleId: $"{worker.Id}-local-state",
            causeEventId: null,
            summary: lostTask is not null
                ? $"{worker.Name} CRASHED while running {lostTask} ({lostAttempt})"
                : $"{worker.Name} CRASHED (offline)",
            plainExplanation: $"{worker.Name} suddenly stopped and lost its local task state. Notice that the Coordinator does NOT know yet — it must wait for missed heartbeats to exceed the {_config.HeartbeatTimeoutMs} ms timeout.",
            payload: new Dictionary<string, string>
            {
                ["WorkerId"] = worker.Id,
                ["InterruptedTask"] = lostTask ?? "None",
                ["InterruptedAttempt"] = lostAttempt ?? "None"
            });
    }

    private EventRecord? HandleRecoverWorker(RecoverWorkerWork work)
    {
        var worker = _workers[work.WorkerId];
        if (!worker.IsCrashed)
        {
            return null;
        }

        worker.IsCrashed = false;
        worker.BootGeneration++;
        worker.Health = NodeHealthStatus.Recovering;
        worker.ActiveTaskId = null;
        worker.ActiveAttemptId = null;

        // Send a fresh heartbeat soon so the Coordinator discovers this worker is back
        EnqueueWork(_currentTimeMs + 40, new EmitHeartbeatWork(worker.Id, worker.BootGeneration));

        return RecordEvent(
            SimulationEventType.WorkerRecovered,
            EventCategory.Faults,
            sourceNodeId: worker.Id,
            targetNodeId: null,
            moduleId: $"{worker.Id}-heartbeat",
            causeEventId: null,
            summary: $"{worker.Name} RECOVERED (sending fresh heartbeat)",
            plainExplanation: $"{worker.Name} came back online in Recovering state. Once its next heartbeat reaches the Coordinator, the Scheduler will allow it to take new tasks again.",
            payload: new Dictionary<string, string>
            {
                ["WorkerId"] = worker.Id,
                ["BootGeneration"] = worker.BootGeneration.ToString()
            });
    }

    private void ScheduleMonitorSweepIfNeeded(long timeMs)
    {
        if (_monitorSweepScheduled)
        {
            return;
        }

        if (_coordinator.WorkerRegistry.Values.Any(r => r.Health != NodeHealthStatus.Failed))
        {
            _monitorSweepScheduled = true;
            EnqueueWork(timeMs, new MonitorSweepWork());
        }
    }

    private EventRecord? HandleMonitorSweep()
    {
        _monitorSweepScheduled = false;

        EventRecord? timeoutEvent = null;

        foreach (var workerId in _workerOrder)
        {
            var reg = _coordinator.WorkerRegistry[workerId];
            if (reg.Health == NodeHealthStatus.Failed)
            {
                continue;
            }

            var silenceMs = _currentTimeMs - reg.LastHeartbeatMs;
            if (silenceMs >= _config.HeartbeatTimeoutMs)
            {
                reg.Health = NodeHealthStatus.Failed;
                _coordinator.TimedOutWorkers++;

                var lostTaskId = reg.AssignedTaskId;
                var lostAttemptId = reg.AssignedAttemptId;
                reg.AssignedTaskId = null;
                reg.AssignedAttemptId = null;

                timeoutEvent = RecordEvent(
                    SimulationEventType.WorkerTimedOut,
                    EventCategory.Faults,
                    sourceNodeId: "coordinator",
                    targetNodeId: workerId,
                    moduleId: "coord-hb-monitor",
                    causeEventId: null,
                    summary: $"Coordinator timed out {reg.WorkerName} (silent for {silenceMs} ms)",
                    plainExplanation: $"No heartbeat arrived from {reg.WorkerName} for {silenceMs} ms (limit: {_config.HeartbeatTimeoutMs} ms). Heartbeat Monitor marked {reg.WorkerName} as Failed in the Worker Registry.",
                    payload: new Dictionary<string, string>
                    {
                        ["WorkerId"] = workerId,
                        ["SilenceMs"] = silenceMs.ToString(),
                        ["LostTaskId"] = lostTaskId ?? "None",
                        ["LostAttemptId"] = lostAttemptId ?? "None"
                    });

                if (lostTaskId is not null && lostAttemptId is not null)
                {
                    EnqueueWork(
                        _currentTimeMs + 10,
                        new RequeueTaskWork(lostTaskId, lostAttemptId, timeoutEvent.Id, $"{reg.WorkerName} heartbeat timeout"));
                }

                ScheduleMonitorSweepIfNeeded(_currentTimeMs + 250);
                return timeoutEvent;
            }

            if (silenceMs >= _config.HeartbeatSuspectMs && reg.Health == NodeHealthStatus.Healthy)
            {
                reg.Health = NodeHealthStatus.Suspected;
            }
        }

        ScheduleMonitorSweepIfNeeded(_currentTimeMs + 250);
        return null;
    }

    private EventRecord? HandleRequeueTask(RequeueTaskWork work)
    {
        if (!_tasks.TryGetValue(work.TaskId, out var task) ||
            task.AcceptedAttemptId is not null ||
            task.CurrentAttemptId != work.AttemptId)
        {
            return null;
        }

        return RequeueLostAttempt(work.TaskId, work.AttemptId, work.CauseEventId, work.Reason);
    }

    private EventRecord? HandleAttemptTimeout(AttemptTimeoutWork work)
    {
        if (!_tasks.TryGetValue(work.TaskId, out var task) ||
            task.AcceptedAttemptId is not null ||
            task.CurrentAttemptId != work.AttemptId)
        {
            return null;
        }

        var attempt = _attempts[work.AttemptId];
        if (attempt.Status is AttemptStatus.Completed or AttemptStatus.Lost or AttemptStatus.StaleIgnored)
        {
            return null;
        }

        // Free worker slot in registry if still bound to this timed-out attempt
        if (_coordinator.WorkerRegistry.TryGetValue(attempt.WorkerId, out var reg) && reg.AssignedAttemptId == attempt.Id)
        {
            reg.AssignedTaskId = null;
            reg.AssignedAttemptId = null;
        }

        return RequeueLostAttempt(
            task.Id,
            attempt.Id,
            work.CauseEventId,
            $"Attempt {attempt.Id} did not finish within {_config.AttemptTimeoutMs} ms (delayed or dropped message)");
    }

    private EventRecord RequeueLostAttempt(string taskId, string attemptId, string? causeEventId, string reason)
    {
        var task = _tasks[taskId];
        var attempt = _attempts[attemptId];

        attempt.Status = AttemptStatus.Lost;
        task.Status = SimulationTaskStatus.Queued;
        task.AssignedWorkerId = null;
        task.CurrentAttemptId = null;

        _retryCount++;
        _coordinator.TaskQueue.Enqueue(taskId);

        var requeueEvent = RecordEvent(
            SimulationEventType.TaskRequeued,
            EventCategory.Faults,
            sourceNodeId: "coordinator",
            targetNodeId: null,
            moduleId: "coord-job-queue",
            causeEventId: causeEventId,
            summary: $"Marked {attemptId} lost and requeued {taskId} (Retry #{_retryCount})",
            plainExplanation: $"Because of {reason}, Coordinator marked Attempt {attemptId} as Lost and put {taskId} back into the Job Queue so a healthy worker can run a fresh attempt.",
            payload: new Dictionary<string, string>
            {
                ["TaskId"] = taskId,
                ["LostAttemptId"] = attemptId,
                ["RetryCount"] = _retryCount.ToString()
            });

        EnqueueWork(_currentTimeMs + 30, new ScheduleNextWork(requeueEvent.Id));
        return requeueEvent;
    }

    private void SendNetworkMessage(
        MessageKind kind,
        string sourceNodeId,
        string targetNodeId,
        string? jobId,
        string? taskId,
        string? attemptId,
        int generation,
        string pillLabel,
        string? causeEventId)
    {
        var msgId = $"msg-{++_nextMessageNum}";
        var isNonHeartbeat = kind != MessageKind.Heartbeat;
        var extraDelay = 0;
        var isDelayed = false;
        var willDrop = false;

        if (isNonHeartbeat && _armedDropCount > 0)
        {
            _armedDropCount--;
            willDrop = true;
        }
        else if (isNonHeartbeat && _armedDelayMs.Count > 0)
        {
            extraDelay = _armedDelayMs.Dequeue();
            isDelayed = true;
        }

        var travelMs = _config.DefaultNetworkLatencyMs + extraDelay;
        var deliverAt = _currentTimeMs + travelMs;
        var finalLabel = isDelayed ? $"{pillLabel} (Delayed)" : pillLabel;

        var msg = new MessageEntity(
            msgId,
            kind,
            sourceNodeId,
            targetNodeId,
            jobId,
            taskId,
            attemptId,
            generation,
            sentAtMs: _currentTimeMs,
            deliverAtMs: deliverAt,
            pillLabel: finalLabel,
            isDelayed: isDelayed,
            causeEventId: causeEventId);

        _inFlightMessages[msgId] = msg;
        _messagesSentCount++;

        // Dropped messages fail visibly halfway through flight
        var deliveryEventTime = willDrop ? _currentTimeMs + Math.Max(30, travelMs / 2) : deliverAt;
        EnqueueWork(deliveryEventTime, new DeliverMessageWork(msgId, willDrop));
    }

    private WorkerRegistryEntry? PickHealthyFreeWorker()
    {
        // Round-robin fairness among healthy idle workers so retries naturally pick a different healthy worker
        var count = _workerOrder.Count;
        for (var i = 0; i < count; i++)
        {
            var idx = (_coordinator.NextRoundRobinIndex + i) % count;
            var reg = _coordinator.WorkerRegistry[_workerOrder[idx]];
            if (reg.Health == NodeHealthStatus.Healthy && reg.AssignedTaskId is null)
            {
                _coordinator.NextRoundRobinIndex = (idx + 1) % count;
                return reg;
            }
        }

        return null;
    }

    private EventRecord RecordEvent(
        SimulationEventType type,
        EventCategory category,
        string sourceNodeId,
        string? targetNodeId,
        string? moduleId,
        string? causeEventId,
        string summary,
        string plainExplanation,
        IReadOnlyDictionary<string, string> payload)
    {
        var seq = ++_nextEventSeq;
        _lastActiveModuleId = moduleId;

        var record = new EventRecord(
            Id: $"evt-{seq}",
            TimestampMs: _currentTimeMs,
            Sequence: seq,
            Type: type,
            Category: category,
            SourceNodeId: sourceNodeId,
            TargetNodeId: targetNodeId,
            ModuleId: moduleId,
            CauseEventId: causeEventId,
            Summary: summary,
            PlainExplanation: plainExplanation,
            Payload: payload);

        _eventLog.Add(record);
        return record;
    }

    private void RebuildSnapshot()
    {
        var nodes = new List<NodeState>
        {
            BuildClientNodeSnapshot(),
            BuildCoordinatorNodeSnapshot()
        };

        foreach (var wid in _workerOrder)
        {
            nodes.Add(BuildWorkerNodeSnapshot(_workers[wid]));
        }

        var links = BuildClusterLinksSnapshot();
        var inFlight = _inFlightMessages.Values
            .OrderBy(m => m.SentAtMs)
            .Select(m => new MessageState(
                m.Id,
                m.Kind,
                m.SourceNodeId,
                m.TargetNodeId,
                m.TaskId,
                m.AttemptId,
                m.SentAtMs,
                m.DeliverAtMs,
                m.Status,
                m.PillLabel,
                m.IsDelayed))
            .ToList();

        var jobs = _jobs.Values
            .Select(j => new JobState(j.Id, j.TaskIds.ToList(), j.Status))
            .ToList();

        var tasks = _tasks.Values
            .Select(t => new TaskItemState(t.Id, t.JobId, t.Status, t.AssignedWorkerId, t.CurrentAttemptId, t.AcceptedAttemptId))
            .ToList();

        var attempts = _attempts.Values
            .Select(a => new AttemptState(a.Id, a.TaskId, a.WorkerId, a.Generation, a.Status))
            .ToList();

        var pending = tasks.Count(t => t.Status == SimulationTaskStatus.Queued);
        var running = tasks.Count(t => t.Status is SimulationTaskStatus.Dispatched or SimulationTaskStatus.Running);
        var completed = tasks.Count(t => t.Status == SimulationTaskStatus.Completed);

        var metrics = new SimulatedMetrics(
            SimulatedTimeMs: _currentTimeMs,
            QueueDepth: _coordinator.TaskQueue.Count,
            PendingTasks: pending,
            RunningTasks: running,
            CompletedTasks: completed,
            DispatchedTasks: _dispatchedCount,
            RetryCount: _retryCount,
            MessagesSent: _messagesSentCount,
            MessagesDropped: _messagesDroppedCount,
            StaleResultsIgnored: _staleResultsIgnoredCount,
            LastJobDurationMs: _lastJobDurationMs,
            ArmedDelayCount: _armedDelayMs.Count,
            ArmedDropCount: _armedDropCount);

        _snapshot = new SimulationState(
            ScenarioId: _config.ScenarioId,
            ScenarioTitle: "Distributed Task Scheduling",
            IsPrototypeStub: false,
            Seed: _seed,
            SimulatedTimeMs: _currentTimeMs,
            Nodes: nodes,
            Links: links,
            InFlightMessages: inFlight,
            Jobs: jobs,
            Tasks: tasks,
            Attempts: attempts,
            EventLog: _eventLog.ToList(),
            Metrics: metrics);
    }

    private NodeState BuildClientNodeSnapshot()
    {
        var openCount = _client.OpenJobs.Count;
        var status = openCount > 0 ? NodeHealthStatus.Processing : NodeHealthStatus.Healthy;
        var summary = openCount > 0
            ? $"Waiting on {openCount} open {(openCount == 1 ? "job" : "jobs")} ({string.Join(", ", _client.OpenJobs)})"
            : _client.CompletedJobs.Count > 0
                ? $"Completed {_client.CompletedJobs.Count} {(_client.CompletedJobs.Count == 1 ? "job" : "jobs")}"
                : "Ready to submit jobs";

        var modules = new List<ModuleState>
        {
            new(
                "client-submitter", "client", "Job Submitter", "Creates new work requests",
                "Packages a user's job request with a stable Job ID and sends it to the Coordinator.",
                _lastActiveModuleId == "client-submitter" ? ModuleStatus.Processing : ModuleStatus.Healthy,
                0.0, 0.0,
                _client.JobsSubmitted > 0 ? [$"Submitted {_client.JobsSubmitted} job(s)"] : [],
                new Dictionary<string, string> { ["Jobs Submitted (Simulated)"] = _client.JobsSubmitted.ToString() },
                _lastActiveModuleId == "client-submitter" ? "Sent job" : null),
            new(
                "client-outbox", "client", "Outbox", "Sends requests to the network",
                "Places outgoing JobSubmit messages onto the simulated network link toward the Coordinator.",
                _inFlightMessages.Values.Any(m => m.SourceNodeId == "client") ? ModuleStatus.Processing : ModuleStatus.Idle,
                2.8, 0.0,
                _inFlightMessages.Values.Where(m => m.SourceNodeId == "client").Select(m => m.PillLabel).ToList(),
                new Dictionary<string, string> { ["Outgoing Jobs (Simulated)"] = _client.JobsSubmitted.ToString() }),
            new(
                "client-inbox", "client", "Response Inbox", "Receives finished results",
                "Listens for final completion replies coming back from the Coordinator.",
                _inFlightMessages.Values.Any(m => m.TargetNodeId == "client") ? ModuleStatus.Processing : ModuleStatus.Idle,
                2.8, 2.6,
                _inFlightMessages.Values.Where(m => m.TargetNodeId == "client").Select(m => m.PillLabel).ToList(),
                new Dictionary<string, string> { ["Replies Received (Simulated)"] = _client.RepliesReceived.ToString() }),
            new(
                "client-tracker", "client", "Job Tracker", "Tracks open & done jobs",
                "Tracks which jobs are still waiting and records completed jobs when final replies arrive.",
                openCount > 0 ? ModuleStatus.Processing : ModuleStatus.Healthy,
                0.0, 2.6,
                openCount > 0
                    ? _client.OpenJobs.Select(j => $"{j}: Waiting for reply").ToList()
                    : _client.CompletedJobs.Select(j => $"{j}: Completed").ToList(),
                new Dictionary<string, string>
                {
                    ["Open Jobs (Simulated)"] = openCount.ToString(),
                    ["Completed Jobs (Simulated)"] = _client.CompletedJobs.Count.ToString(),
                    ["Last Job Duration (Simulated)"] = _lastJobDurationMs.HasValue ? $"{_lastJobDurationMs.Value} ms" : "-"
                },
                openCount > 0 ? $"{openCount} open" : null)
        };

        var links = new List<ModuleLink>
        {
            new("cl-1", "client-submitter", "client-outbox", "Submit Job"),
            new("cl-2", "client-inbox", "client-tracker", "Final Result", IsDashed: true)
        };

        return new NodeState(
            "client", "Client", NodeRole.Client, status, 4, openCount, summary,
            "The user's machine that sends jobs into the cluster and waits for final answers.",
            -2.6, 0.5, modules, links,
            _client.CompletedJobs.TakeLast(3).Select(j => $"Completed {j}").ToList(),
            _client.OpenJobs.TakeLast(3).Select(j => $"Submitted {j}").ToList());
    }

    private NodeState BuildCoordinatorNodeSnapshot()
    {
        var queuedList = _coordinator.TaskQueue.ToList();
        var runningCount = _tasks.Values.Count(t => t.Status is SimulationTaskStatus.Dispatched or SimulationTaskStatus.Running);
        var healthyWorkers = _coordinator.WorkerRegistry.Values.Count(r => r.Health == NodeHealthStatus.Healthy);
        var suspectedWorkers = _coordinator.WorkerRegistry.Values.Count(r => r.Health == NodeHealthStatus.Suspected);

        var coordHealth = runningCount > 0 || queuedList.Count > 0
            ? NodeHealthStatus.Processing
            : suspectedWorkers > 0
                ? NodeHealthStatus.Suspected
                : NodeHealthStatus.Healthy;

        var summary = $"{queuedList.Count} queued · {runningCount} running · {healthyWorkers}/{_workerOrder.Count} healthy workers";

        var registryLines = _workerOrder
            .Select(id =>
            {
                var r = _coordinator.WorkerRegistry[id];
                var assignment = r.AssignedTaskId is null ? "Idle" : $"Running {r.AssignedTaskId}/{r.AssignedAttemptId}";
                return $"{r.WorkerName}: {r.Health} ({assignment}, HB @ {r.LastHeartbeatMs} ms)";
            })
            .ToList();

        var modules = new List<ModuleState>
        {
            new(
                "coord-req-handler", "coordinator", "Request Handler", "Accepts client jobs",
                "Receives incoming jobs from the Client, creates tracked tasks, and hands them to the Job Queue.",
                _lastActiveModuleId == "coord-req-handler" ? ModuleStatus.Processing : ModuleStatus.Healthy,
                0.0, 0.0,
                _coordinator.RecentInbox.ToList(),
                new Dictionary<string, string>
                {
                    ["Jobs Accepted (Simulated)"] = _coordinator.JobsAccepted.ToString(),
                    ["Tasks Created (Simulated)"] = _tasks.Count.ToString()
                }),
            new(
                "coord-job-queue", "coordinator", "Job Queue", "Holds waiting tasks",
                "Waiting line for tasks not yet sent to a worker, including tasks requeued after timeouts or crashes.",
                queuedList.Count > 0 ? ModuleStatus.Queued : ModuleStatus.Idle,
                2.9, 0.0,
                queuedList.Select(tid => $"{tid} (Job {_tasks[tid].JobId}) - Waiting").ToList(),
                new Dictionary<string, string>
                {
                    ["Queue Depth (Simulated)"] = queuedList.Count.ToString(),
                    ["Total Enqueued (Simulated)"] = _coordinator.TotalEnqueued.ToString()
                },
                queuedList.Count > 0 ? $"{queuedList.Count} waiting" : null),
            new(
                "coord-scheduler", "coordinator", "Scheduler", "Picks a healthy worker",
                "Selects a healthy idle worker from the Worker Registry, tags each dispatch with a new Attempt generation, and sends it out.",
                _lastActiveModuleId == "coord-scheduler" ? ModuleStatus.Processing : ModuleStatus.Healthy,
                5.8, 0.0,
                _coordinator.RecentOutbox.ToList(),
                new Dictionary<string, string>
                {
                    ["Dispatches (Simulated)"] = _dispatchedCount.ToString(),
                    ["Retries (Simulated)"] = _retryCount.ToString()
                },
                _retryCount > 0 ? $"{_retryCount} retry" : null),
            new(
                "coord-outbox", "coordinator", "Outbox", "Sends dispatches & replies",
                "Puts task assignments and final client completion replies onto the simulated network.",
                _inFlightMessages.Values.Any(m => m.SourceNodeId == "coordinator") ? ModuleStatus.Processing : ModuleStatus.Healthy,
                8.7, 0.0,
                _inFlightMessages.Values.Where(m => m.SourceNodeId == "coordinator").Select(m => m.PillLabel).ToList(),
                new Dictionary<string, string>
                {
                    ["In-Flight Outbound"] = _inFlightMessages.Values.Count(m => m.SourceNodeId == "coordinator").ToString()
                }),
            new(
                "coord-hb-monitor", "coordinator", "Heartbeat Monitor", "Watches worker health",
                $"Listens for worker heartbeats. After {_config.HeartbeatSuspectMs} ms of silence it marks a worker Suspected; after {_config.HeartbeatTimeoutMs} ms it times them out and requeues work.",
                suspectedWorkers > 0 ? ModuleStatus.Suspected : (_coordinator.TimedOutWorkers > 0 ? ModuleStatus.Processing : ModuleStatus.Healthy),
                2.9, 3.0,
                _workerOrder.Select(id =>
                {
                    var r = _coordinator.WorkerRegistry[id];
                    var ago = _currentTimeMs - r.LastHeartbeatMs;
                    return $"{r.WorkerName}: last HB {ago} ms ago ({r.Health})";
                }).ToList(),
                new Dictionary<string, string>
                {
                    ["Timeout Policy (Simulated)"] = $"{_config.HeartbeatTimeoutMs} ms",
                    ["Worker Timeouts"] = _coordinator.TimedOutWorkers.ToString()
                },
                suspectedWorkers > 0 ? "Suspecting worker" : null),
            new(
                "coord-registry", "coordinator", "Worker Registry", "Directory of workers",
                "Stores each worker's known health status, last heartbeat timestamp, and assigned task.",
                healthyWorkers == 0 ? ModuleStatus.Failed : ModuleStatus.Healthy,
                5.8, 3.0,
                registryLines,
                new Dictionary<string, string>
                {
                    ["Healthy Workers"] = $"{healthyWorkers} / {_workerOrder.Count}",
                    ["Free Healthy Slots"] = _coordinator.WorkerRegistry.Values.Count(r => r.Health == NodeHealthStatus.Healthy && r.AssignedTaskId is null).ToString()
                }),
            new(
                "coord-result-handler", "coordinator", "Result Handler", "Checks & accepts results",
                "Verifies every result's Attempt ID so stale late replies from older attempts can never overwrite an accepted result.",
                _lastActiveModuleId == "coord-result-handler" ? ModuleStatus.Processing : ModuleStatus.Healthy,
                8.7, 3.0,
                _tasks.Values.Where(t => t.AcceptedAttemptId is not null).TakeLast(4).Select(t => $"Accepted {t.Id} ({t.AcceptedAttemptId})").ToList(),
                new Dictionary<string, string>
                {
                    ["Accepted Results"] = _coordinator.AcceptedResults.ToString(),
                    ["Stale Results Ignored"] = _staleResultsIgnoredCount.ToString()
                },
                _staleResultsIgnoredCount > 0 ? $"{_staleResultsIgnoredCount} stale ignored" : null)
        };

        var links = new List<ModuleLink>
        {
            new("co-1", "coord-req-handler", "coord-job-queue", "Enqueue"),
            new("co-2", "coord-job-queue", "coord-scheduler", "Next Task"),
            new("co-3", "coord-scheduler", "coord-outbox", "Dispatch"),
            new("co-4", "coord-hb-monitor", "coord-registry", "Update Health", IsDashed: true),
            new("co-5", "coord-registry", "coord-scheduler", "Healthy List", IsDashed: true),
            new("co-6", "coord-result-handler", "coord-outbox", "Client Reply", IsDashed: true)
        };

        return new NodeState(
            "coordinator", "Coordinator", NodeRole.Coordinator, coordHealth, _workerOrder.Count, runningCount, summary,
            "The central organizer. It accepts jobs, keeps the waiting queue, tracks worker heartbeats, and reassigns work if a worker fails.",
            0.2, 0.4, modules, links,
            _coordinator.RecentInbox.ToList(),
            _coordinator.RecentOutbox.ToList());
    }

    private NodeState BuildWorkerNodeSnapshot(WorkerEntity w)
    {
        var isBusy = w.ActiveTaskId is not null;
        var reg = _coordinator.WorkerRegistry[w.Id];

        // Compute simulated utilization honestly from accumulated simulated busy intervals
        var liveBusy = w.TotalBusyMs + (w.BusySinceMs.HasValue ? _currentTimeMs - w.BusySinceMs.Value : 0);
        var utilizationPct = _currentTimeMs > 0 ? Math.Round(100.0 * liveBusy / _currentTimeMs, 1) : 0.0;

        var activeDesc = isBusy ? $"{w.ActiveTaskId} ({w.ActiveAttemptId})" : null;
        var statusSummary = w.IsCrashed
            ? $"Crashed (Coordinator sees: {reg.Health})"
            : w.Health == NodeHealthStatus.Recovering
                ? "Recovering · waiting for Coordinator heartbeat ack"
                : isBusy
                    ? $"Running {activeDesc}"
                    : "Healthy · Idle (Ready for work)";

        var modBaseStatus = w.IsCrashed ? ModuleStatus.Failed : ModuleStatus.Idle;

        var modules = new List<ModuleState>
        {
            new(
                $"{w.Id}-inbox", w.Id, "Inbox", "Receives task dispatches",
                "Receives task assignment messages coming from the Coordinator and passes them to the Task Executor.",
                w.IsCrashed ? ModuleStatus.Failed : (isBusy ? ModuleStatus.Healthy : ModuleStatus.Idle),
                0.0, 0.0,
                w.RecentInbox.ToList(),
                new Dictionary<string, string> { ["Dispatches Received (Simulated)"] = w.MessagesReceived.ToString() }),
            new(
                $"{w.Id}-executor", w.Id, "Task Executor", "Runs assigned tasks",
                "Performs simulated work for one task at a time and creates a completion result stamped with its Attempt ID.",
                w.IsCrashed ? ModuleStatus.Failed : (isBusy ? ModuleStatus.Processing : ModuleStatus.Idle),
                2.9, 0.0,
                w.IsCrashed ? ["Offline (Worker crashed)"] : (isBusy ? [$"Running {activeDesc}"] : ["Idle (Ready)"]),
                new Dictionary<string, string>
                {
                    ["Active Task"] = activeDesc ?? "None",
                    ["Tasks Completed (Simulated)"] = w.TasksCompleted.ToString(),
                    ["Busy Utilization (Simulated)"] = $"{utilizationPct:0.0}%"
                },
                w.IsCrashed ? "Crashed" : (isBusy ? $"Running {w.ActiveTaskId}" : null)),
            new(
                $"{w.Id}-outbox", w.Id, "Result Outbox", "Sends finished results",
                "Sends finished results and periodic heartbeats onto the simulated network toward the Coordinator.",
                w.IsCrashed ? ModuleStatus.Failed : (_inFlightMessages.Values.Any(m => m.SourceNodeId == w.Id) ? ModuleStatus.Processing : ModuleStatus.Idle),
                5.8, 0.0,
                w.RecentOutbox.ToList(),
                new Dictionary<string, string> { ["Results Sent (Simulated)"] = w.ResultsSent.ToString() }),
            new(
                $"{w.Id}-local-state", w.Id, "Local Task State", "Tracks local progress",
                "Stores the task attempt running in local memory. Lost immediately if this worker crashes.",
                w.IsCrashed ? ModuleStatus.Failed : (isBusy ? ModuleStatus.Processing : ModuleStatus.Idle),
                2.9, 3.0,
                w.IsCrashed ? ["Local state lost on crash"] : (isBusy ? [$"Tracking {activeDesc}"] : ["Empty"]),
                new Dictionary<string, string>
                {
                    ["Used Capacity"] = isBusy ? "1 / 1" : "0 / 1",
                    ["Boot Generation"] = w.BootGeneration.ToString()
                }),
            new(
                $"{w.Id}-heartbeat", w.Id, "Heartbeat Sender", "Sends 'I am alive' pings",
                $"Sends a heartbeat every {_config.HeartbeatIntervalMs} ms. Stops when the worker crashes.",
                w.IsCrashed ? ModuleStatus.Failed : (w.Health == NodeHealthStatus.Recovering ? ModuleStatus.Recovering : ModuleStatus.Healthy),
                5.8, 3.0,
                w.IsCrashed ? ["Stopped (Worker crashed)"] : [$"Last ping @ {w.LastHeartbeatSentMs} ms"],
                new Dictionary<string, string>
                {
                    ["Heartbeats Sent (Simulated)"] = w.HeartbeatsSent.ToString(),
                    ["Interval (Simulated)"] = $"{_config.HeartbeatIntervalMs} ms"
                })
        };

        var links = new List<ModuleLink>
        {
            new($"{w.Id}-ml-1", $"{w.Id}-inbox", $"{w.Id}-executor", "Start Task"),
            new($"{w.Id}-ml-2", $"{w.Id}-executor", $"{w.Id}-outbox", "Result"),
            new($"{w.Id}-ml-3", $"{w.Id}-executor", $"{w.Id}-local-state", "Update State", IsDashed: true),
            new($"{w.Id}-ml-4", $"{w.Id}-heartbeat", $"{w.Id}-outbox", "Ping", IsDashed: true)
        };

        _ = modBaseStatus;

        return new NodeState(
            w.Id, w.Name, NodeRole.Worker, w.Health, 1, isBusy ? 1 : 0, statusSummary,
            "A worker machine that executes tasks assigned by the Coordinator and sends periodic heartbeats.",
            w.GridX, w.GridY, modules, links,
            w.RecentInbox.ToList(),
            w.RecentOutbox.ToList());
    }

    private List<LinkState> BuildClusterLinksSnapshot()
    {
        var links = new List<LinkState>();

        var clientMsg = _inFlightMessages.Values.FirstOrDefault(m =>
            (m.SourceNodeId == "client" && m.TargetNodeId == "coordinator") ||
            (m.SourceNodeId == "coordinator" && m.TargetNodeId == "client"));

        links.Add(new LinkState(
            "link-c-coord",
            "client",
            "coordinator",
            _config.DefaultNetworkLatencyMs,
            clientMsg?.PillLabel ?? "Jobs & Replies",
            IsHeartbeatChannel: false,
            IsActive: clientMsg is not null));

        foreach (var wid in _workerOrder)
        {
            var reg = _coordinator.WorkerRegistry[wid];
            var worker = _workers[wid];
            var activeMsg = _inFlightMessages.Values.FirstOrDefault(m =>
                (m.SourceNodeId == "coordinator" && m.TargetNodeId == wid) ||
                (m.SourceNodeId == wid && m.TargetNodeId == "coordinator"));

            var isTaskTraffic = activeMsg is not null && activeMsg.Kind != MessageKind.Heartbeat;
            var label = activeMsg is not null
                ? activeMsg.PillLabel
                : worker.IsCrashed
                    ? (reg.Health == NodeHealthStatus.Failed ? "Timed Out" : reg.Health == NodeHealthStatus.Suspected ? "No Heartbeat..." : "Worker Offline")
                    : reg.AssignedTaskId is not null
                        ? $"Running {reg.AssignedTaskId}"
                        : "Heartbeat · OK";

            links.Add(new LinkState(
                $"link-coord-{wid}",
                "coordinator",
                wid,
                _config.DefaultNetworkLatencyMs,
                label,
                IsHeartbeatChannel: !isTaskTraffic,
                IsActive: isTaskTraffic));
        }

        return links;
    }

    private string FormatNodeName(string nodeId) => nodeId switch
    {
        "client" => "Client",
        "coordinator" => "Coordinator",
        _ => _workers.TryGetValue(nodeId, out var w) ? w.Name : nodeId
    };

    private static string ShortWorkerTag(string workerId) =>
        workerId.StartsWith("worker-", StringComparison.OrdinalIgnoreCase)
            ? "W" + workerId["worker-".Length..]
            : workerId;

    private static void TrimBuffer(Queue<string> queue)
    {
        while (queue.Count > 4)
        {
            queue.Dequeue();
        }
    }

    // Internal scheduled work items
    private abstract class ScheduledWork
    {
        public long ScheduledTimeMs { get; set; }
    }

    private sealed class SubmitJobWork(string label, int taskCount) : ScheduledWork
    {
        public string Label { get; } = label;
        public int TaskCount { get; } = taskCount;
    }

    private sealed class DeliverMessageWork(string messageId, bool willDrop) : ScheduledWork
    {
        public string MessageId { get; } = messageId;
        public bool WillDrop { get; } = willDrop;
    }

    private sealed class EnqueueTasksWork(string jobId, string causeEventId) : ScheduledWork
    {
        public string JobId { get; } = jobId;
        public string CauseEventId { get; } = causeEventId;
    }

    private sealed class ScheduleNextWork(string? causeEventId) : ScheduledWork
    {
        public string? CauseEventId { get; } = causeEventId;
    }

    private sealed class CompleteTaskWork(string workerId, string taskId, string attemptId, int generation, string? causeEventId) : ScheduledWork
    {
        public string WorkerId { get; } = workerId;
        public string TaskId { get; } = taskId;
        public string AttemptId { get; } = attemptId;
        public int Generation { get; } = generation;
        public string? CauseEventId { get; } = causeEventId;
    }

    private sealed class CrashWorkerWork(string workerId) : ScheduledWork
    {
        public string WorkerId { get; } = workerId;
    }

    private sealed class RecoverWorkerWork(string workerId) : ScheduledWork
    {
        public string WorkerId { get; } = workerId;
    }

    private sealed class EmitHeartbeatWork(string workerId, int bootGeneration) : ScheduledWork
    {
        public string WorkerId { get; } = workerId;
        public int BootGeneration { get; } = bootGeneration;
    }

    private sealed class MonitorSweepWork : ScheduledWork;

    private sealed class RequeueTaskWork(string taskId, string attemptId, string? causeEventId, string reason) : ScheduledWork
    {
        public string TaskId { get; } = taskId;
        public string AttemptId { get; } = attemptId;
        public string? CauseEventId { get; } = causeEventId;
        public string Reason { get; } = reason;
    }

    private sealed class FinishJobWork(string jobId, string? causeEventId) : ScheduledWork
    {
        public string JobId { get; } = jobId;
        public string? CauseEventId { get; } = causeEventId;
    }

    private sealed class AttemptTimeoutWork(string taskId, string attemptId, string? causeEventId) : ScheduledWork
    {
        public string TaskId { get; } = taskId;
        public string AttemptId { get; } = attemptId;
        public string? CauseEventId { get; } = causeEventId;
    }

    // Internal mutable entities (never exposed outside engine)
    private sealed class ClientEntity
    {
        public int JobsSubmitted { get; set; }
        public int RepliesReceived { get; set; }
        public List<string> OpenJobs { get; } = [];
        public List<string> CompletedJobs { get; } = [];
    }

    private sealed class CoordinatorEntity
    {
        public int JobsAccepted { get; set; }
        public int TotalEnqueued { get; set; }
        public int AcceptedResults { get; set; }
        public int TimedOutWorkers { get; set; }
        public int NextRoundRobinIndex { get; set; }
        public Queue<string> TaskQueue { get; } = new();
        public Dictionary<string, WorkerRegistryEntry> WorkerRegistry { get; } = new();
        public Queue<string> RecentInbox { get; } = new();
        public Queue<string> RecentOutbox { get; } = new();
    }

    private sealed class WorkerRegistryEntry(
        string workerId,
        string workerName,
        NodeHealthStatus health,
        long lastHeartbeatMs,
        string? assignedTaskId,
        string? assignedAttemptId)
    {
        public string WorkerId { get; } = workerId;
        public string WorkerName { get; } = workerName;
        public NodeHealthStatus Health { get; set; } = health;
        public long LastHeartbeatMs { get; set; } = lastHeartbeatMs;
        public string? AssignedTaskId { get; set; } = assignedTaskId;
        public string? AssignedAttemptId { get; set; } = assignedAttemptId;
    }

    private sealed class WorkerEntity(string id, string name, double gridX, double gridY)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public double GridX { get; } = gridX;
        public double GridY { get; } = gridY;
        public bool IsCrashed { get; set; }
        public int BootGeneration { get; set; } = 1;
        public NodeHealthStatus Health { get; set; } = NodeHealthStatus.Healthy;
        public string? ActiveTaskId { get; set; }
        public string? ActiveAttemptId { get; set; }
        public long? BusySinceMs { get; set; }
        public long TotalBusyMs { get; set; }
        public int MessagesReceived { get; set; }
        public int TasksCompleted { get; set; }
        public int ResultsSent { get; set; }
        public int HeartbeatsSent { get; set; }
        public long LastHeartbeatSentMs { get; set; }
        public Queue<string> RecentInbox { get; } = new();
        public Queue<string> RecentOutbox { get; } = new();
    }

    private sealed class JobEntity(string id, List<string> taskIds, long submittedAtMs)
    {
        public string Id { get; } = id;
        public List<string> TaskIds { get; } = taskIds;
        public long SubmittedAtMs { get; } = submittedAtMs;
        public long? CompletedAtMs { get; set; }
        public SimulationTaskStatus Status { get; set; } = SimulationTaskStatus.Queued;
    }

    private sealed class TaskEntity(string id, string jobId)
    {
        public string Id { get; } = id;
        public string JobId { get; } = jobId;
        public SimulationTaskStatus Status { get; set; } = SimulationTaskStatus.Queued;
        public string? AssignedWorkerId { get; set; }
        public string? CurrentAttemptId { get; set; }
        public string? AcceptedAttemptId { get; set; }
        public int NextGeneration { get; set; }
    }

    private sealed class AttemptEntity(string id, string taskId, string workerId, int generation, AttemptStatus status, long createdAtMs)
    {
        public string Id { get; } = id;
        public string TaskId { get; } = taskId;
        public string WorkerId { get; } = workerId;
        public int Generation { get; } = generation;
        public AttemptStatus Status { get; set; } = status;
        public long CreatedAtMs { get; } = createdAtMs;
    }

    private sealed class MessageEntity(
        string id,
        MessageKind kind,
        string sourceNodeId,
        string targetNodeId,
        string? jobId,
        string? taskId,
        string? attemptId,
        int generation,
        long sentAtMs,
        long deliverAtMs,
        string pillLabel,
        bool isDelayed,
        string? causeEventId)
    {
        public string Id { get; } = id;
        public MessageKind Kind { get; } = kind;
        public string SourceNodeId { get; } = sourceNodeId;
        public string TargetNodeId { get; } = targetNodeId;
        public string? JobId { get; } = jobId;
        public string? TaskId { get; } = taskId;
        public string? AttemptId { get; } = attemptId;
        public int Generation { get; } = generation;
        public long SentAtMs { get; } = sentAtMs;
        public long DeliverAtMs { get; } = deliverAtMs;
        public string PillLabel { get; } = pillLabel;
        public bool IsDelayed { get; } = isDelayed;
        public string? CauseEventId { get; } = causeEventId;
        public MessageDeliveryStatus Status { get; set; } = MessageDeliveryStatus.InFlight;
    }
}
