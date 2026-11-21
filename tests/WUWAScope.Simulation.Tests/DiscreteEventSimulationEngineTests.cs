using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Engine;
using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Tests;

public sealed class DiscreteEventSimulationEngineTests
{
    [Fact]
    public void HappyPath_CompletesJobEndToEndWithCausalTraceAndReconciledCounts()
    {
        var engine = new DiscreteEventSimulationEngine(new ScenarioConfig(), seed: 42);
        Assert.False(engine.IsPrototypeStub);

        var prevTime = -1L;
        var prevSeq = 0L;
        var completed = false;

        for (var i = 0; i < 60; i++)
        {
            var prevLogCount = engine.GetState().EventLog.Count;
            var step = engine.Step();
            Assert.True(step.Executed);
            Assert.NotNull(step.Event);
            Assert.Equal(prevLogCount + 1, step.State.EventLog.Count);

            // Strict deterministic ordering: (TimestampMs, Sequence)
            Assert.True(step.Event.TimestampMs >= prevTime);
            Assert.True(step.Event.Sequence > prevSeq);
            prevTime = step.Event.TimestampMs;
            prevSeq = step.Event.Sequence;

            // Task count conservation invariant after EVERY single step
            var m = step.State.Metrics;
            Assert.Equal(step.State.Tasks.Count, m.PendingTasks + m.RunningTasks + m.CompletedTasks);

            if (step.Event.Type == SimulationEventType.ClientResponded)
            {
                completed = true;
                break;
            }
        }

        Assert.True(completed, "Expected happy path to reach ClientResponded.");

        var finalState = engine.GetState();
        var types = finalState.EventLog.Select(e => e.Type).ToList();
        Assert.Contains(SimulationEventType.JobSubmitted, types);
        Assert.Contains(SimulationEventType.JobReceived, types);
        Assert.Contains(SimulationEventType.TaskEnqueued, types);
        Assert.Contains(SimulationEventType.TaskDispatched, types);
        Assert.Contains(SimulationEventType.TaskStarted, types);
        Assert.Contains(SimulationEventType.TaskExecutionFinished, types);
        Assert.Contains(SimulationEventType.ResultReceived, types);
        Assert.Contains(SimulationEventType.JobFinished, types);
        Assert.Contains(SimulationEventType.ClientResponded, types);

        var task = Assert.Single(finalState.Tasks);
        Assert.Equal(SimulationTaskStatus.Completed, task.Status);
        Assert.Equal("A1", task.AcceptedAttemptId);
        Assert.True(finalState.Metrics.LastJobDurationMs > 0);
    }

    [Fact]
    public void ResetAndReplay_WithSameSeedAndActions_ProducesIdenticalEventTrace()
    {
        List<(string Id, long Time, long Seq, SimulationEventType Type, string Src, string? Dst, string Summary)> RunScript(DiscreteEventSimulationEngine engine)
        {
            for (var i = 0; i < 40; i++)
            {
                if (i == 6)
                {
                    engine.DispatchAction(new CrashWorkerAction("worker-1"));
                }
                else if (i == 18)
                {
                    engine.DispatchAction(new RecoverWorkerAction("worker-1"));
                }

                engine.Step();
            }

            return engine.GetState().EventLog
                .Select(e => (e.Id, e.TimestampMs, e.Sequence, e.Type, e.SourceNodeId, e.TargetNodeId, e.Summary))
                .ToList();
        }

        var engineA = new DiscreteEventSimulationEngine(new ScenarioConfig(), seed: 777);
        var trace1 = RunScript(engineA);

        engineA.Reset();
        var traceAfterReset = RunScript(engineA);

        var engineB = new DiscreteEventSimulationEngine(new ScenarioConfig(), seed: 777);
        var traceFresh = RunScript(engineB);

        Assert.Equal(trace1, traceAfterReset);
        Assert.Equal(trace1, traceFresh);
    }

    [Fact]
    public void WorkerCrash_TriggersHeartbeatTimeoutRequeueAndHealthyWorkerCompletion()
    {
        var engine = new DiscreteEventSimulationEngine(new ScenarioConfig(), seed: 42);

        // Step until Worker 1 starts T1
        while (true)
        {
            var s = engine.Step();
            if (s.Event?.Type == SimulationEventType.TaskStarted)
            {
                break;
            }
        }

        var startedTask = Assert.Single(engine.GetState().Tasks);
        var crashedWorkerId = startedTask.AssignedWorkerId!;
        var crashStartTime = engine.GetState().SimulatedTimeMs;

        engine.DispatchAction(new CrashWorkerAction(crashedWorkerId));

        var sawCrash = false;
        var sawTimeout = false;
        var sawRequeue = false;
        var completed = false;
        long timeoutTime = 0;

        for (var i = 0; i < 120; i++)
        {
            var s = engine.Step();
            Assert.True(s.Executed);

            // Crashed worker must NEVER receive a new task while offline
            var crashedWorker = Assert.Single(s.State.Nodes, n => n.Id == crashedWorkerId);
            if (sawCrash)
            {
                Assert.Equal(NodeHealthStatus.Failed, crashedWorker.Health);
                Assert.Equal(0, crashedWorker.ActiveTaskCount);
            }

            if (s.Event?.Type == SimulationEventType.WorkerCrashed)
            {
                sawCrash = true;
            }
            else if (s.Event?.Type == SimulationEventType.WorkerTimedOut)
            {
                sawTimeout = true;
                timeoutTime = s.Event.TimestampMs;
            }
            else if (s.Event?.Type == SimulationEventType.TaskRequeued)
            {
                sawRequeue = true;
            }
            else if (s.Event?.Type == SimulationEventType.ClientResponded)
            {
                completed = true;
                break;
            }
        }

        Assert.True(sawCrash);
        Assert.True(sawTimeout);
        Assert.True(sawRequeue);
        Assert.True(completed);

        // Verify no magical instant failover: timeout occurred meaningfully after crash
        Assert.True(timeoutTime > crashStartTime + 500, "Timeout must happen only after missed heartbeats exceed timeout threshold.");

        var finalState = engine.GetState();
        var task = Assert.Single(finalState.Tasks);
        Assert.Equal(SimulationTaskStatus.Completed, task.Status);
        Assert.NotEqual("A1", task.AcceptedAttemptId);
        Assert.NotEqual(crashedWorkerId, task.AssignedWorkerId);
        Assert.True(finalState.Metrics.RetryCount >= 1);

        var attempt1 = Assert.Single(finalState.Attempts, a => a.Generation == 1);
        var attempt2 = Assert.Single(finalState.Attempts, a => a.Generation == 2);
        Assert.Equal(AttemptStatus.Lost, attempt1.Status);
        Assert.Equal(AttemptStatus.Completed, attempt2.Status);
        Assert.Equal(attempt2.Id, task.AcceptedAttemptId);
    }

    [Fact]
    public void DelayedResultMessage_CausesRetryAndSafelyIgnoresStaleResult()
    {
        var config = new ScenarioConfig(
            DefaultNetworkLatencyMs: 100,
            TaskExecutionDurationMs: 500,
            AttemptTimeoutMs: 1400);
        var engine = new DiscreteEventSimulationEngine(config, seed: 12);

        // Advance until Worker 1 starts Task T1 (Attempt A1)
        while (true)
        {
            var s = engine.Step();
            if (s.Event?.Type == SimulationEventType.TaskStarted)
            {
                break;
            }
        }

        // Arm a large delay right before Worker 1 sends its TaskResult for Attempt A1
        engine.DispatchAction(new DelayNextMessageAction(ExtraDelayMs: 2600));

        var sawStaleIgnored = false;
        string? acceptedAttemptWhenDone = null;

        for (var i = 0; i < 160; i++)
        {
            var s = engine.Step();
            if (!s.Executed)
            {
                break;
            }

            if (s.Event?.Type == SimulationEventType.ResultReceived && acceptedAttemptWhenDone is null)
            {
                acceptedAttemptWhenDone = s.State.Tasks[0].AcceptedAttemptId;
            }

            if (s.Event?.Type == SimulationEventType.StaleResultIgnored)
            {
                sawStaleIgnored = true;
                break;
            }
        }

        Assert.True(sawStaleIgnored, "Expected delayed A1 result to arrive after A2 was accepted and be ignored as stale.");
        Assert.Equal("A2", acceptedAttemptWhenDone);

        var finalState = engine.GetState();
        var task = Assert.Single(finalState.Tasks);
        Assert.Equal("A2", task.AcceptedAttemptId); // Stale A1 must NEVER overwrite A2!
        Assert.Equal(1, finalState.Metrics.StaleResultsIgnored);

        var attempt1 = Assert.Single(finalState.Attempts, a => a.Id == "A1");
        Assert.Equal(AttemptStatus.StaleIgnored, attempt1.Status);
    }

    [Fact]
    public void DroppedDispatchMessage_RecoversViaAttemptTimeoutAndCompletes()
    {
        var config = new ScenarioConfig(AttemptTimeoutMs: 1200);
        var engine = new DiscreteEventSimulationEngine(config, seed: 5);

        // Advance until TaskEnqueued, then arm DropNextMessage before TaskDispatched sends the dispatch packet
        while (true)
        {
            var s = engine.Step();
            if (s.Event?.Type == SimulationEventType.TaskEnqueued)
            {
                break;
            }
        }

        engine.DispatchAction(new DropNextMessageAction());

        var sawDrop = false;
        var completed = false;

        for (var i = 0; i < 140; i++)
        {
            var s = engine.Step();
            if (s.Event?.Type == SimulationEventType.MessageDropped)
            {
                sawDrop = true;
            }

            if (s.Event?.Type == SimulationEventType.ClientResponded)
            {
                completed = true;
                break;
            }
        }

        Assert.True(sawDrop);
        Assert.True(completed);
        Assert.True(engine.GetState().Metrics.MessagesDropped >= 1);
        Assert.True(engine.GetState().Metrics.RetryCount >= 1);
    }

    [Fact]
    public void WorkerRecovery_ReturnsWorkerToHealthyAndReceivesFutureWork()
    {
        var engine = new DiscreteEventSimulationEngine(new ScenarioConfig(WorkerCount: 1, AutoSubmitInitialJob: false), seed: 10);

        engine.DispatchAction(new CrashWorkerAction("worker-1"));
        var sawTimeout = false;
        for (var i = 0; i < 40; i++)
        {
            var s = engine.Step();
            if (s.Event?.Type == SimulationEventType.WorkerTimedOut)
            {
                sawTimeout = true;
                break;
            }
        }
        Assert.True(sawTimeout);

        // Submit job while only worker is crashed -> must stay queued
        engine.DispatchAction(new SubmitJobAction("Job After Crash", TaskCount: 1));
        for (var i = 0; i < 6; i++)
        {
            engine.Step();
        }
        Assert.Equal(1, engine.GetState().Metrics.QueueDepth);

        // Recover worker -> heartbeat restores Healthy -> queued task completes!
        engine.DispatchAction(new RecoverWorkerAction("worker-1"));
        var completed = false;
        for (var i = 0; i < 60; i++)
        {
            var s = engine.Step();
            if (s.Event?.Type == SimulationEventType.ClientResponded)
            {
                completed = true;
                break;
            }
        }

        Assert.True(completed);
        Assert.Equal(NodeHealthStatus.Healthy, Assert.Single(engine.GetState().Nodes, n => n.Id == "worker-1").Health);
    }
}
