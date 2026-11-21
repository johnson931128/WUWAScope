using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Contracts;

public sealed record ScenarioConfig(
    string ScenarioId = "task-scheduling",
    int WorkerCount = 3,
    int DefaultNetworkLatencyMs = 120,
    int TaskExecutionDurationMs = 650,
    int HeartbeatIntervalMs = 500,
    int HeartbeatSuspectMs = 950,
    int HeartbeatTimeoutMs = 1500,
    int AttemptTimeoutMs = 2200,
    bool AutoSubmitInitialJob = true);

public sealed record ScenarioDescriptor(
    string Id,
    string Title,
    string ShortSummary,
    bool IsAvailable,
    string TheoryNoteTitle,
    string TheoryNoteUrl,
    string SimplificationsNote);

public abstract record UserAction;

public sealed record SubmitJobAction(string JobLabel, int TaskCount = 1) : UserAction;

public sealed record CrashWorkerAction(string WorkerId) : UserAction;

public sealed record RecoverWorkerAction(string WorkerId) : UserAction;

public sealed record DelayNextMessageAction(int ExtraDelayMs = 600) : UserAction;

public sealed record DropNextMessageAction : UserAction;

// Phase 2: Logical Clocks actions
public sealed record TriggerLocalEventAction(string ProcessId) : UserAction;

public sealed record SendPeerMessageAction(string SourceId, string TargetId) : UserAction;

// Phase 2: Global Snapshot (Chandy-Lamport) actions
public sealed record InitiateSnapshotAction(string InitiatorId) : UserAction;

public sealed record SendTransferAction(string SourceId, string TargetId, int Amount = 50) : UserAction;

// Phase 2: Raft Leader Election actions
public sealed record TriggerElectionTimeoutAction(string NodeId) : UserAction;

public sealed record StepResult(
    bool Executed,
    EventRecord? Event,
    SimulationState State,
    string? Note = null);

/// <summary>
/// Core contract implemented by both the Phase 0 prototype stub and the Phase 1 deterministic discrete-event engine.
/// Completely independent of Avalonia, UI threads, and wall-clock time.
/// </summary>
public interface ISimulationEngine
{
    bool IsPrototypeStub { get; }

    SimulationState GetState();

    long? PeekNextEventTimeMs();

    StepResult Step();

    SimulationState Reset();

    void DispatchAction(UserAction action);

    event EventHandler<SimulationState>? StateChanged;
}
