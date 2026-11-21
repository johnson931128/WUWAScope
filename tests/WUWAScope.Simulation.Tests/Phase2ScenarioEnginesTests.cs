using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Engine;
using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Tests;

public sealed class Phase2ScenarioEnginesTests
{
    [Fact]
    public void LogicalClocks_ReproducesExactLectureTimelineFromNote01()
    {
        var engine = new LogicalClocksEngine(seed: 42);

        for (var i = 0; i < 40; i++)
        {
            var step = engine.Step();
            if (!step.Executed)
            {
                break;
            }
        }

        var state = engine.GetState();
        var p1 = Assert.Single(state.Nodes, n => n.Id == "p1");
        var p2 = Assert.Single(state.Nodes, n => n.Id == "p2");
        var p3 = Assert.Single(state.Nodes, n => n.Id == "p3");

        // Exact final states verified against Distributed-System notes/01 §3.2 & §4.2
        Assert.Equal("L=9 · V=[5,3,4]", p1.StatusSummary);
        Assert.Equal("L=11 · V=[5,6,4]", p2.StatusSummary);
        Assert.Equal("L=7 · V=[2,3,4]", p3.StatusSummary);

        // Clock Condition invariant: every received message has AfterLamport > IncomingLamport
        var receives = state.EventLog.Where(e => e.Type == SimulationEventType.LogicalMessageReceived).ToList();
        Assert.Equal(4, receives.Count);
        foreach (var r in receives)
        {
            var incoming = int.Parse(r.Payload["IncomingLamport"]);
            var after = int.Parse(r.Payload["AfterLamport"]);
            var before = int.Parse(r.Payload["BeforeLamport"]);
            Assert.True(after > incoming, "Receive Lamport timestamp must strictly exceed send timestamp.");
            Assert.True(after > before, "Local Lamport clock must strictly increase on receive.");
        }
    }

    [Fact]
    public void LogicalClocks_VectorComparisonMatchesNote01Examples()
    {
        Assert.Equal(VectorClockRelation.HappensBefore, LogicalClocksEngine.CompareVectors([2, 1, 0], [2, 2, 0]));
        Assert.Equal(VectorClockRelation.Concurrent, LogicalClocksEngine.CompareVectors([2, 1, 0], [1, 2, 0]));
        Assert.Equal(VectorClockRelation.Concurrent, LogicalClocksEngine.CompareVectors([3, 0, 0], [2, 3, 0]));
        Assert.Equal(VectorClockRelation.HappensBefore, LogicalClocksEngine.CompareVectors([2, 3, 0], [4, 3, 4]));
        Assert.Equal(VectorClockRelation.Equal, LogicalClocksEngine.CompareVectors([1, 2, 3], [1, 2, 3]));
    }

    [Fact]
    public void GlobalSnapshot_ConservesExactTotalMoneyWithInFlightTransfers()
    {
        var engine = new GlobalSnapshotEngine(seed: 42);

        var sawCompleted = false;
        for (var i = 0; i < 60; i++)
        {
            var step = engine.Step();
            if (!step.Executed)
            {
                break;
            }

            if (step.Event?.Type == SimulationEventType.SnapshotCompleted)
            {
                sawCompleted = true;
                break;
            }
        }

        Assert.True(sawCompleted, "Expected Chandy-Lamport snapshot to complete.");
        Assert.Equal(engine.InitialTotalMoney, engine.LastSnapshotTotal);
        Assert.True(engine.LastSnapshotChannelSum > 0, "Expected at least one in-flight transfer to be captured in channel state SC.");
        Assert.Equal(engine.InitialTotalMoney, engine.LastSnapshotLocalSum + engine.LastSnapshotChannelSum);
    }

    [Fact]
    public void GlobalSnapshot_PreservesConservationAcrossDelayedFifoAndSecondSnapshot()
    {
        var engine = new GlobalSnapshotEngine(seed: 7, preloadWalkthrough: false);

        engine.DispatchAction(new DelayNextMessageAction(ExtraDelayMs: 400));
        engine.DispatchAction(new InitiateSnapshotAction("acct-a"));
        engine.Step(); // SnapshotInitiated at A

        // Send transfers while markers are in flight
        engine.DispatchAction(new SendTransferAction("acct-b", "acct-a", 50));
        engine.DispatchAction(new SendTransferAction("acct-c", "acct-b", 50));

        var sawCompleted = false;
        for (var i = 0; i < 80; i++)
        {
            var s = engine.Step();
            if (!s.Executed) break;
            if (s.Event?.Type == SimulationEventType.SnapshotCompleted)
            {
                sawCompleted = true;
                break;
            }
        }

        Assert.True(sawCompleted);
        Assert.Equal(1000, engine.LastSnapshotTotal);
    }

    [Fact]
    public void Raft_ElectsLeaderAndFailsOverToHigherTermWhenLeaderCrashes()
    {
        var engine = new RaftElectionEngine(seed: 42);

        string? firstLeaderId = null;
        var firstLeaderTerm = 0;

        for (var i = 0; i < 80; i++)
        {
            var s = engine.Step();
            Assert.True(s.Executed);
            if (s.Event?.Type == SimulationEventType.LeaderElected)
            {
                firstLeaderId = s.Event.SourceNodeId;
                firstLeaderTerm = int.Parse(s.Event.Payload["Term"]);
                break;
            }
        }

        Assert.NotNull(firstLeaderId);
        Assert.True(firstLeaderTerm >= 1);

        // Crash the elected Leader
        engine.DispatchAction(new CrashWorkerAction(firstLeaderId));

        string? secondLeaderId = null;
        var secondLeaderTerm = 0;

        for (var i = 0; i < 160; i++)
        {
            var s = engine.Step();
            if (!s.Executed) break;
            if (s.Event?.Type == SimulationEventType.LeaderElected)
            {
                secondLeaderId = s.Event.SourceNodeId;
                secondLeaderTerm = int.Parse(s.Event.Payload["Term"]);
                break;
            }
        }

        Assert.NotNull(secondLeaderId);
        Assert.NotEqual(firstLeaderId, secondLeaderId);
        Assert.True(secondLeaderTerm > firstLeaderTerm, "New leader must be elected in a strictly higher Term.");

        // Election Safety invariant: at most 1 leader per Term
        foreach (var kv in engine.LeadersPerTerm)
        {
            Assert.True(kv.Value.Count <= 1, $"Term {kv.Key} violated Election Safety with {kv.Value.Count} leaders.");
        }
    }

    [Fact]
    public void Raft_LosingMajorityQuorumBlocksElectionUntilRecovery()
    {
        var engine = new RaftElectionEngine(seed: 99);

        // Crash 3 of 5 servers -> only 2 remain alive (< Quorum 3)
        engine.DispatchAction(new CrashWorkerAction("node-1"));
        engine.DispatchAction(new CrashWorkerAction("node-2"));
        engine.DispatchAction(new CrashWorkerAction("node-3"));

        var electedWithoutQuorum = false;
        for (var i = 0; i < 80; i++)
        {
            var s = engine.Step();
            if (!s.Executed) break;
            if (s.Event?.Type == SimulationEventType.LeaderElected)
            {
                electedWithoutQuorum = true;
                break;
            }
        }

        Assert.False(electedWithoutQuorum, "Raft must NEVER elect a Leader with only 2 of 5 servers alive.");

        // Recover 1 server (3 alive == majority quorum!) -> Leader election must now succeed
        engine.DispatchAction(new RecoverWorkerAction("node-3"));

        var electedAfterRecovery = false;
        for (var i = 0; i < 160; i++)
        {
            var s = engine.Step();
            if (!s.Executed) break;
            if (s.Event?.Type == SimulationEventType.LeaderElected)
            {
                electedAfterRecovery = true;
                break;
            }
        }

        Assert.True(electedAfterRecovery, "Expected Leader election to succeed once majority quorum (3/5) is restored.");
    }

    [Fact]
    public void Raft_ResetAndReplayAreDeterministic()
    {
        List<(string Id, long Time, SimulationEventType Type, string Src)> Run(RaftElectionEngine e)
        {
            for (var i = 0; i < 45; i++)
            {
                e.Step();
            }

            return e.GetState().EventLog.Select(x => (x.Id, x.TimestampMs, x.Type, x.SourceNodeId)).ToList();
        }

        var e1 = new RaftElectionEngine(seed: 123);
        var t1 = Run(e1);
        e1.Reset();
        var t2 = Run(e1);
        var t3 = Run(new RaftElectionEngine(seed: 123));

        Assert.Equal(t1, t2);
        Assert.Equal(t1, t3);
    }
}
