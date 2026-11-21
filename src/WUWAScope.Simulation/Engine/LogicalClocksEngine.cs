using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Engine;

public enum VectorClockRelation
{
    Equal,
    HappensBefore,
    HappensAfter,
    Concurrent
}

/// <summary>
/// Pure C# deterministic simulator for Lamport Logical Clocks and Vector Clocks.
/// Follows Distributed-System notes/01 §3 and §4 rules and preloads the verified 3-process lecture timeline (P1, P2, P3).
/// </summary>
public sealed class LogicalClocksEngine : ISimulationEngine
{
    private readonly int _seed;
    private readonly int _defaultLatencyMs;
    private readonly bool _preloadLectureTimeline;

    private PriorityQueue<ClockWork, (long TimeMs, long Seq)> _queue = null!;
    private long _currentTimeMs;
    private long _nextQueueSeq;
    private long _nextEventSeq;
    private int _nextMsgNum;
    private readonly Queue<int> _armedDelays = new();

    private readonly ProcessEntity[] _processes = new ProcessEntity[3];
    private readonly Dictionary<string, int> _idToIndex = new();
    private readonly Dictionary<string, ClockMessageEntity> _inFlight = new();
    private readonly List<EventRecord> _eventLog = [];
    private int _messagesSent;
    private string? _lastActiveModuleId;
    private SimulationState _snapshot = null!;

    public LogicalClocksEngine(int seed = 42, int defaultLatencyMs = 140, bool preloadLectureTimeline = true)
    {
        _seed = seed;
        _defaultLatencyMs = defaultLatencyMs;
        _preloadLectureTimeline = preloadLectureTimeline;
        Initialize();
    }

    public bool IsPrototypeStub => false;

    public event EventHandler<SimulationState>? StateChanged;

    public SimulationState GetState() => _snapshot;

    public static VectorClockRelation CompareVectors(IReadOnlyList<int> x, IReadOnlyList<int> y)
    {
        if (x.Count != y.Count)
        {
            throw new ArgumentException("Vector clocks must have identical dimensions.");
        }

        var xLeqY = true;
        var yLeqX = true;

        for (var i = 0; i < x.Count; i++)
        {
            if (x[i] > y[i]) xLeqY = false;
            if (y[i] > x[i]) yLeqX = false;
        }

        if (xLeqY && yLeqX) return VectorClockRelation.Equal;
        if (xLeqY) return VectorClockRelation.HappensBefore;
        if (yLeqX) return VectorClockRelation.HappensAfter;
        return VectorClockRelation.Concurrent;
    }

    public long? PeekNextEventTimeMs() =>
        _queue.TryPeek(out var next, out _) ? next.TimeMs : null;

    public SimulationState Reset()
    {
        Initialize();
        StateChanged?.Invoke(this, _snapshot);
        return _snapshot;
    }

    public void DispatchAction(UserAction action)
    {
        switch (action)
        {
            case TriggerLocalEventAction local when _idToIndex.ContainsKey(local.ProcessId):
                Enqueue(_currentTimeMs, new LocalWork(local.ProcessId, "User local computation"));
                break;

            case SendPeerMessageAction send when _idToIndex.ContainsKey(send.SourceId) && _idToIndex.ContainsKey(send.TargetId) && send.SourceId != send.TargetId:
                Enqueue(_currentTimeMs, new SendWork(send.SourceId, send.TargetId, "User message"));
                break;

            case DelayNextMessageAction delay:
                _armedDelays.Enqueue(Math.Max(200, delay.ExtraDelayMs));
                RebuildSnapshot();
                StateChanged?.Invoke(this, _snapshot);
                break;
        }
    }

    public StepResult Step()
    {
        if (!_queue.TryDequeue(out var work, out _))
        {
            return new StepResult(false, null, _snapshot, "Timeline finished! Click '+ Local Event' or 'Send Msg' to keep exploring.");
        }

        _currentTimeMs = Math.Max(_currentTimeMs, work.TimeMs);
        EventRecord? evt = work switch
        {
            LocalWork local => HandleLocalEvent(local),
            SendWork send => HandleSend(send),
            DeliverWork deliver => HandleDeliver(deliver),
            _ => null
        };

        RebuildSnapshot();
        StateChanged?.Invoke(this, _snapshot);
        return new StepResult(evt is not null, evt, _snapshot);
    }

    private void Initialize()
    {
        _queue = new PriorityQueue<ClockWork, (long TimeMs, long Seq)>();
        _currentTimeMs = 0;
        _nextQueueSeq = 0;
        _nextEventSeq = 0;
        _nextMsgNum = 0;
        _armedDelays.Clear();
        _inFlight.Clear();
        _eventLog.Clear();
        _messagesSent = 0;
        _lastActiveModuleId = null;

        _processes[0] = new ProcessEntity(0, "p1", "Process P1", -1.4, -0.4);
        _processes[1] = new ProcessEntity(1, "p2", "Process P2", 2.4, -0.4);
        _processes[2] = new ProcessEntity(2, "p3", "Process P3", 1.2, 2.8);

        _idToIndex.Clear();
        foreach (var p in _processes)
        {
            _idToIndex[p.Id] = p.Index;
        }

        if (_preloadLectureTimeline)
        {
            // Exact verified 3-process timeline from Distributed-System notes/01 §3.2 & §4.2:
            // Initial local events on P1, P2, P3 -> [1,0,0], [0,1,0], [0,0,1]
            Enqueue(0, new LocalWork("p1", "Initial local step"));
            Enqueue(40, new LocalWork("p2", "Initial local step"));
            Enqueue(80, new LocalWork("p3", "Initial local step"));

            // Arrow 1: P1 sends [2,0,0] (L=2) -> arrives at P2 as [2,2,0] (L=3)
            Enqueue(160, new SendWork("p1", "p2", "m1 (Lecture arrow 1)", FixedLatencyMs: 140));
            // Arrow 2: P2 sends [2,3,0] (L=4) -> arrives at P3 as [2,3,2] (L=5)
            Enqueue(380, new SendWork("p2", "p3", "m2 (Lecture arrow 2)", FixedLatencyMs: 140));

            // Concurrent local steps while m2 is traveling/delivered:
            // P1 local [3,0,0] (L=3) is concurrent with P2's [2,3,0]!
            Enqueue(440, new LocalWork("p1", "Independent work (concurrent with P2)"));
            Enqueue(500, new LocalWork("p2", "Local step on P2"));
            Enqueue(600, new LocalWork("p3", "Local step on P3"));

            // Arrow 3: P3 sends [2,3,4] (L=7) -> arrives at P1 as [4,3,4] (L=8)
            Enqueue(700, new SendWork("p3", "p1", "m3 (Lecture arrow 3)", FixedLatencyMs: 140));
            // Arrow 4: P1 sends [5,3,4] (L=9) -> arrives at P2 as [5,5,4] (L=10)
            Enqueue(940, new SendWork("p1", "p2", "m4 (Lecture arrow 4)", FixedLatencyMs: 140));
            // Final local step on P2 -> [5,6,4] (L=11)
            Enqueue(1180, new LocalWork("p2", "Final local step on P2"));
        }

        RebuildSnapshot();
    }

    private void Enqueue(long timeMs, ClockWork work)
    {
        work.TimeMs = timeMs;
        _queue.Enqueue(work, (timeMs, ++_nextQueueSeq));
    }

    private EventRecord HandleLocalEvent(LocalWork work)
    {
        var p = _processes[_idToIndex[work.ProcessId]];
        p.Lamport += 1;
        p.Vector[p.Index] += 1;
        p.LocalEvents++;
        var vecText = FormatVector(p.Vector);
        p.History.Add($"local → L={p.Lamport}, V={vecText}");

        return RecordEvent(
            SimulationEventType.LocalEvent,
            EventCategory.Protocol,
            p.Id,
            null,
            $"{p.Id}-clock",
            null,
            $"{p.Name} local event → L={p.Lamport}, V={vecText}",
            $"{p.Name} ran a local step ({work.Label}). Both its Lamport counter and its own Vector slot increased by 1: L={p.Lamport}, V={vecText}.",
            new Dictionary<string, string>
            {
                ["Process"] = p.Name,
                ["Lamport"] = p.Lamport.ToString(),
                ["Vector"] = vecText
            });
    }

    private EventRecord HandleSend(SendWork work)
    {
        var src = _processes[_idToIndex[work.SourceId]];
        var dst = _processes[_idToIndex[work.TargetId]];

        // Rule: increment BEFORE attaching copies to the outgoing message
        src.Lamport += 1;
        src.Vector[src.Index] += 1;
        src.MessagesSent++;

        var attachedLamport = src.Lamport;
        var attachedVector = (int[])src.Vector.Clone();
        var vecText = FormatVector(attachedVector);
        src.History.Add($"send → {dst.Name}: L={attachedLamport}, V={vecText}");

        var extraDelay = _armedDelays.Count > 0 ? _armedDelays.Dequeue() : 0;
        var latency = (work.FixedLatencyMs ?? _defaultLatencyMs) + extraDelay;
        var msgId = $"cmsg-{++_nextMsgNum}";
        var isDelayed = extraDelay > 0;

        var evt = RecordEvent(
            SimulationEventType.LogicalMessageSent,
            EventCategory.Network,
            src.Id,
            dst.Id,
            $"{src.Id}-outbox",
            null,
            $"{src.Name} → {dst.Name} sent L={attachedLamport}, V={vecText}",
            $"{src.Name} incremented its clock first and attached a copy (L={attachedLamport}, V={vecText}) to the outgoing message for {dst.Name}.",
            new Dictionary<string, string>
            {
                ["MessageId"] = msgId,
                ["From"] = src.Name,
                ["To"] = dst.Name,
                ["AttachedLamport"] = attachedLamport.ToString(),
                ["AttachedVector"] = vecText
            });

        var msg = new ClockMessageEntity(
            msgId,
            src.Id,
            dst.Id,
            attachedLamport,
            attachedVector,
            _currentTimeMs,
            _currentTimeMs + latency,
            isDelayed ? $"L={attachedLamport} {vecText} (Delayed)" : $"L={attachedLamport} {vecText}",
            isDelayed,
            evt.Id);

        _inFlight[msgId] = msg;
        _messagesSent++;
        Enqueue(msg.DeliverAtMs, new DeliverWork(msgId));
        return evt;
    }

    private EventRecord? HandleDeliver(DeliverWork work)
    {
        if (!_inFlight.Remove(work.MessageId, out var msg))
        {
            return null;
        }

        var dst = _processes[_idToIndex[msg.TargetId]];
        var src = _processes[_idToIndex[msg.SourceId]];

        var beforeLamport = dst.Lamport;
        var beforeVector = (int[])dst.Vector.Clone();
        var relationBefore = CompareVectors(beforeVector, msg.AttachedVector);

        // Exact Lamport rule: L_i := max(L_i, t) + 1
        dst.Lamport = Math.Max(dst.Lamport, msg.AttachedLamport) + 1;

        // Exact Vector rule: component-wise max, then V_i[i] += 1
        for (var k = 0; k < dst.Vector.Length; k++)
        {
            dst.Vector[k] = Math.Max(dst.Vector[k], msg.AttachedVector[k]);
        }
        dst.Vector[dst.Index] += 1;
        dst.MessagesReceived++;

        var afterVecText = FormatVector(dst.Vector);
        var beforeVecText = FormatVector(beforeVector);
        var msgVecText = FormatVector(msg.AttachedVector);
        dst.History.Add($"recv ← {src.Name}: L={dst.Lamport}, V={afterVecText}");

        var relNote = relationBefore == VectorClockRelation.Concurrent
            ? $"Before merging, {dst.Name}'s {beforeVecText} and incoming {msgVecText} were CONCURRENT (∥) — neither knew about the other yet."
            : $"Before merging, relation was {relationBefore}.";

        return RecordEvent(
            SimulationEventType.LogicalMessageReceived,
            EventCategory.Protocol,
            dst.Id,
            src.Id,
            $"{dst.Id}-inbox",
            msg.CauseEventId,
            $"{dst.Name} received from {src.Name} → L={dst.Lamport}, V={afterVecText}",
            $"{dst.Name} merged max({beforeLamport}, {msg.AttachedLamport}) + 1 = {dst.Lamport} and element-wise max({beforeVecText}, {msgVecText}) + self = {afterVecText}. {relNote}",
            new Dictionary<string, string>
            {
                ["Receiver"] = dst.Name,
                ["Sender"] = src.Name,
                ["BeforeLamport"] = beforeLamport.ToString(),
                ["IncomingLamport"] = msg.AttachedLamport.ToString(),
                ["AfterLamport"] = dst.Lamport.ToString(),
                ["AfterVector"] = afterVecText,
                ["PreMergeRelation"] = relationBefore.ToString()
            });
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
        var evt = new EventRecord(
            $"evt-{seq}",
            _currentTimeMs,
            seq,
            type,
            category,
            sourceNodeId,
            targetNodeId,
            moduleId,
            causeEventId,
            summary,
            plainExplanation,
            payload);
        _eventLog.Add(evt);
        return evt;
    }

    private void RebuildSnapshot()
    {
        var nodes = _processes.Select(BuildProcessNode).ToList();
        var links = new List<LinkState>
        {
            BuildPairLink("lc-p1-p2", "p1", "p2"),
            BuildPairLink("lc-p2-p3", "p2", "p3"),
            BuildPairLink("lc-p3-p1", "p3", "p1")
        };

        var inFlight = _inFlight.Values
            .OrderBy(m => m.SentAtMs)
            .Select(m => new MessageState(
                m.Id,
                MessageKind.PeerMessage,
                m.SourceId,
                m.TargetId,
                null,
                null,
                m.SentAtMs,
                m.DeliverAtMs,
                MessageDeliveryStatus.InFlight,
                m.PillLabel,
                m.IsDelayed))
            .ToList();

        var metrics = new SimulatedMetrics(
            SimulatedTimeMs: _currentTimeMs,
            QueueDepth: _queue.Count,
            PendingTasks: 0,
            RunningTasks: _inFlight.Count,
            CompletedTasks: _eventLog.Count,
            DispatchedTasks: _messagesSent,
            RetryCount: 0,
            MessagesSent: _messagesSent,
            MessagesDropped: 0,
            ArmedDelayCount: _armedDelays.Count);

        _snapshot = new SimulationState(
            ScenarioId: "logical-clocks",
            ScenarioTitle: "Logical Clocks (Lamport & Vector)",
            IsPrototypeStub: false,
            Seed: _seed,
            SimulatedTimeMs: _currentTimeMs,
            Nodes: nodes,
            Links: links,
            InFlightMessages: inFlight,
            Jobs: [],
            Tasks: [],
            Attempts: [],
            EventLog: _eventLog.ToList(),
            Metrics: metrics);
    }

    private NodeState BuildProcessNode(ProcessEntity p)
    {
        var vecText = FormatVector(p.Vector);
        var isSendingOrReceiving = _inFlight.Values.Any(m => m.SourceId == p.Id || m.TargetId == p.Id);
        var health = isSendingOrReceiving ? NodeHealthStatus.Processing : NodeHealthStatus.Healthy;

        var modules = new List<ModuleState>
        {
            new(
                $"{p.Id}-clock", p.Id, "Clock Register", "Stores L_i and V_i",
                "Holds this process's Lamport counter (single integer) and Vector Clock [P1, P2, P3]. Increments on every local, send, or receive event.",
                _lastActiveModuleId == $"{p.Id}-clock" ? ModuleStatus.Processing : ModuleStatus.Healthy,
                0.0, 0.0,
                [$"Lamport L = {p.Lamport}", $"Vector V = {vecText}"],
                new Dictionary<string, string>
                {
                    ["Lamport Clock"] = p.Lamport.ToString(),
                    ["Vector Clock"] = vecText
                },
                $"L={p.Lamport} {vecText}"),
            new(
                $"{p.Id}-history", p.Id, "Event History", "Local causal log",
                "Records every timestamped event that happened on this process so you can compare causality (a → b) vs concurrency (a ∥ b).",
                ModuleStatus.Healthy,
                2.9, 0.0,
                p.History.TakeLast(5).Reverse().ToList(),
                new Dictionary<string, string>
                {
                    ["Local Events"] = p.LocalEvents.ToString(),
                    ["Total Events"] = p.History.Count.ToString()
                }),
            new(
                $"{p.Id}-inbox", p.Id, "Message Inbox", "Merges incoming clocks",
                "When a message arrives with (t, W), computes max(L, t)+1 and component-wise max(V, W) then increments own slot.",
                _lastActiveModuleId == $"{p.Id}-inbox" ? ModuleStatus.Processing : ModuleStatus.Idle,
                0.0, 2.8,
                _inFlight.Values.Where(m => m.TargetId == p.Id).Select(m => $"In flight: {m.PillLabel}").ToList(),
                new Dictionary<string, string> { ["Messages Received"] = p.MessagesReceived.ToString() }),
            new(
                $"{p.Id}-outbox", p.Id, "Message Outbox", "Stamps outgoing copies",
                "Attaches an immutable copy of (L_i, V_i) after incrementing so later local changes never alter an already-sent timestamp.",
                _lastActiveModuleId == $"{p.Id}-outbox" ? ModuleStatus.Processing : ModuleStatus.Idle,
                2.9, 2.8,
                _inFlight.Values.Where(m => m.SourceId == p.Id).Select(m => $"Sent: {m.PillLabel}").ToList(),
                new Dictionary<string, string> { ["Messages Sent"] = p.MessagesSent.ToString() })
        };

        var links = new List<ModuleLink>
        {
            new($"{p.Id}-l1", $"{p.Id}-inbox", $"{p.Id}-clock", "Merge max()+1"),
            new($"{p.Id}-l2", $"{p.Id}-clock", $"{p.Id}-history", "Record"),
            new($"{p.Id}-l3", $"{p.Id}-clock", $"{p.Id}-outbox", "Copy (L, V)", IsDashed: true)
        };

        return new NodeState(
            p.Id,
            p.Name,
            NodeRole.Process,
            health,
            1,
            p.Lamport,
            $"L={p.Lamport} · V={vecText}",
            "An independent process with no shared physical clock. Tracks causal history using Lamport and Vector Clocks.",
            p.GridX,
            p.GridY,
            modules,
            links,
            p.History.TakeLast(3).Reverse().ToList(),
            [$"Current L={p.Lamport}, V={vecText}"]);
    }

    private LinkState BuildPairLink(string id, string a, string b)
    {
        var active = _inFlight.Values.FirstOrDefault(m =>
            (m.SourceId == a && m.TargetId == b) || (m.SourceId == b && m.TargetId == a));

        return new LinkState(
            id,
            a,
            b,
            _defaultLatencyMs,
            active?.PillLabel ?? "Async Channel",
            IsHeartbeatChannel: active is null,
            IsActive: active is not null);
    }

    public static string FormatVector(IReadOnlyList<int> v) => $"[{string.Join(",", v)}]";

    private abstract class ClockWork
    {
        public long TimeMs { get; set; }
    }

    private sealed class LocalWork(string processId, string label) : ClockWork
    {
        public string ProcessId { get; } = processId;
        public string Label { get; } = label;
    }

    private sealed class SendWork(string sourceId, string targetId, string label, int? FixedLatencyMs = null) : ClockWork
    {
        public string SourceId { get; } = sourceId;
        public string TargetId { get; } = targetId;
        public string Label { get; } = label;
        public int? FixedLatencyMs { get; } = FixedLatencyMs;
    }

    private sealed class DeliverWork(string messageId) : ClockWork
    {
        public string MessageId { get; } = messageId;
    }

    private sealed class ProcessEntity(int index, string id, string name, double gridX, double gridY)
    {
        public int Index { get; } = index;
        public string Id { get; } = id;
        public string Name { get; } = name;
        public double GridX { get; } = gridX;
        public double GridY { get; } = gridY;
        public int Lamport { get; set; }
        public int[] Vector { get; } = new int[3];
        public int LocalEvents { get; set; }
        public int MessagesSent { get; set; }
        public int MessagesReceived { get; set; }
        public List<string> History { get; } = [];
    }

    private sealed class ClockMessageEntity(
        string id,
        string sourceId,
        string targetId,
        int attachedLamport,
        int[] attachedVector,
        long sentAtMs,
        long deliverAtMs,
        string pillLabel,
        bool isDelayed,
        string? causeEventId)
    {
        public string Id { get; } = id;
        public string SourceId { get; } = sourceId;
        public string TargetId { get; } = targetId;
        public int AttachedLamport { get; } = attachedLamport;
        public int[] AttachedVector { get; } = attachedVector;
        public long SentAtMs { get; } = sentAtMs;
        public long DeliverAtMs { get; } = deliverAtMs;
        public string PillLabel { get; } = pillLabel;
        public bool IsDelayed { get; } = isDelayed;
        public string? CauseEventId { get; } = causeEventId;
    }
}
