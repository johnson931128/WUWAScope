using WUWAScope.Simulation.Models;
using WUWAScope.Simulation.Stubs;

namespace WUWAScope.Simulation.Tests;

public sealed class PrototypeStubEngineTests
{
    [Fact]
    public void InitialState_ContainsFiveNodesWithExpectedRoles()
    {
        var engine = new PrototypeStubEngine();
        var state = engine.GetState();

        Assert.True(engine.IsPrototypeStub);
        Assert.True(state.IsPrototypeStub);
        Assert.Equal(5, state.Nodes.Count);
        Assert.Equal(1, state.Nodes.Count(n => n.Role == NodeRole.Client));
        Assert.Equal(1, state.Nodes.Count(n => n.Role == NodeRole.Coordinator));
        Assert.Equal(3, state.Nodes.Count(n => n.Role == NodeRole.Worker));
    }

    [Fact]
    public void CoordinatorAndWorker_HaveDistinctInternalModuleStructures()
    {
        var state = new PrototypeStubEngine().GetState();
        var coordinator = Assert.Single(state.Nodes, n => n.Role == NodeRole.Coordinator);
        var worker = state.Nodes.First(n => n.Role == NodeRole.Worker);

        var coordNames = coordinator.Modules.Select(m => m.Name).ToHashSet();
        var workerNames = worker.Modules.Select(m => m.Name).ToHashSet();

        Assert.Contains("Scheduler", coordNames);
        Assert.Contains("Job Queue", coordNames);
        Assert.Contains("Heartbeat Monitor", coordNames);

        Assert.Contains("Task Executor", workerNames);
        Assert.Contains("Heartbeat Sender", workerNames);
        Assert.DoesNotContain("Scheduler", workerNames);
    }

    [Fact]
    public void Step_DoesNotFakeLiveSimulationInPhase0()
    {
        var engine = new PrototypeStubEngine();
        var before = engine.GetState();

        var result = engine.Step();

        Assert.False(result.Executed);
        Assert.Null(result.Event);
        Assert.Equal(before.SimulatedTimeMs, result.State.SimulatedTimeMs);
        Assert.False(string.IsNullOrWhiteSpace(result.Note));
    }

    [Fact]
    public void Reset_RestoresIdenticalSeedAndEventLog()
    {
        var engine = new PrototypeStubEngine(seed: 99);
        var initial = engine.GetState();
        var afterReset = engine.Reset();

        Assert.Equal(99, afterReset.Seed);
        Assert.Equal(initial.SimulatedTimeMs, afterReset.SimulatedTimeMs);
        Assert.Equal(initial.EventLog.Count, afterReset.EventLog.Count);
        Assert.Equal(
            initial.EventLog.Select(e => (e.Id, e.TimestampMs, e.Sequence, e.Type)),
            afterReset.EventLog.Select(e => (e.Id, e.TimestampMs, e.Sequence, e.Type)));
    }

    [Fact]
    public void SimulationAssembly_HasZeroAvaloniaReferences()
    {
        var referenced = typeof(PrototypeStubEngine).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain(referenced, name => name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }
}
