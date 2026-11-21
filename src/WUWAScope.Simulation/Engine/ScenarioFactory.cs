using WUWAScope.Simulation.Contracts;

namespace WUWAScope.Simulation.Engine;

/// <summary>
/// Creates pure C# deterministic simulation engines for each verified scenario.
/// </summary>
public static class ScenarioFactory
{
    public static ISimulationEngine Create(string scenarioId, int seed = 42) => scenarioId switch
    {
        "task-scheduling" => new DiscreteEventSimulationEngine(new ScenarioConfig(), seed),
        "logical-clocks" => new LogicalClocksEngine(seed),
        "global-snapshots" => new GlobalSnapshotEngine(seed),
        "raft-leader-election" => new RaftElectionEngine(seed),
        _ => throw new ArgumentException($"Unknown scenario ID: '{scenarioId}'.", nameof(scenarioId))
    };
}
