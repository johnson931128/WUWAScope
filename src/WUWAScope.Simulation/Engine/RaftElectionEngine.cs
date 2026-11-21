using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Engine;

public enum RaftRole
{
    Follower,
    Candidate,
    Leader
}

/// <summary>
/// Pure C# deterministic Raft Leader Election simulator (5 voting servers, majority quorum = 3).
/// Follows Distributed-System notes/02 §4 & §11.1–11.2: Terms, randomized election timeouts,
/// RequestVote, majority quorum, higher-term step-down, and crash/recovery with durable Term & VotedFor.
/// </summary>
public sealed class RaftElectionEngine : ISimulationEngine
{
    public const int ServerCount = 5;
    public const int QuorumSize = 3;

    private readonly int _seed;
    private readonly int _networkLatencyMs;
    private readonly int _heartbeatIntervalMs;
    private readonly int _electionTimeoutMinMs;
    private readonly int _electionTimeoutMaxMs;

    private Random _rng = null!;
    private PriorityQueue<RaftWork, (long TimeMs, long Seq)> _queue = null!;
    private long _currentTimeMs;
    private long _nextQueueSeq;
    private long _nextEventSeq;
    private int _nextMsgNum;

    private readonly Queue<int> _armedDelays = new();
    private int _armedDropCount;

    private readonly Dictionary<string, RaftNodeEntity> _nodes = new();
    private readonly List<string> _nodeOrder = [];
    private readonly Dictionary<string, RaftMessageEntity> _inFlight = new();
    private readonly List<EventRecord> _eventLog = [];

    // Protocol verification history (used by invariants & tests)
    private readonly Dictionary<int, HashSet<string>> _leadersPerTerm = new();
    private int _messagesSent;
    private int _messagesDropped;
    private int _electionsStarted;
    private int _leadersElected;
    private string? _lastActiveModuleId;
    private SimulationState _snapshot = null!;

    public RaftElectionEngine(
        int seed = 42,
        int networkLatencyMs = 75,
        int heartbeatIntervalMs = 240,
        int electionTimeoutMinMs = 460,
        int electionTimeoutMaxMs = 920)
    {
        _seed = seed;
        _networkLatencyMs = networkLatencyMs;
        _heartbeatIntervalMs = heartbeatIntervalMs;
        _electionTimeoutMinMs = electionTimeoutMinMs;
        _electionTimeoutMaxMs = electionTimeoutMaxMs;
        Initialize();
    }

    public bool IsPrototypeStub => false;

    public IReadOnlyDictionary<int, IReadOnlyCollection<string>> LeadersPerTerm =>
        _leadersPerTerm.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value.ToList());

    public event EventHandler<SimulationState>? StateChanged;

    public SimulationState GetState() => _snapshot;

    public long? PeekNextEventTimeMs()
    {
        while (_queue.TryPeek(out var next, out _))
        {
            if (IsWorkStillValid(next))
            {
                return next.TimeMs;
            }

            _queue.Dequeue();
        }

        return null;
    }

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
            case CrashWorkerAction crash when _nodes.ContainsKey(crash.WorkerId):
                Enqueue(_currentTimeMs, new CrashNodeWork(crash.WorkerId));
                break;

            case RecoverWorkerAction rec when _nodes.ContainsKey(rec.WorkerId):
                Enqueue(_currentTimeMs, new RecoverNodeWork(rec.WorkerId));
                break;

            case TriggerElectionTimeoutAction timeout when _nodes.TryGetValue(timeout.NodeId, out var node) && !node.IsCrashed:
                node.TimerToken++;
                Enqueue(_currentTimeMs, new ElectionTimeoutWork(node.Id, node.TimerToken));
                break;

            case DelayNextMessageAction delay:
                _armedDelays.Enqueue(Math.Max(200, delay.ExtraDelayMs));
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

            _currentTimeMs = Math.Max(_currentTimeMs, work.TimeMs);
            EventRecord? evt = work switch
            {
                ElectionTimeoutWork t => HandleElectionTimeout(t),
                LeaderHeartbeatWork hb => HandleLeaderHeartbeat(hb),
                DeliverRaftMsgWork d => HandleDeliverMessage(d),
                CrashNodeWork c => HandleCrashNode(c),
                RecoverNodeWork r => HandleRecoverNode(r),
                _ => null
            };

            if (evt is null)
            {
                continue;
            }

            RebuildSnapshot();
            StateChanged?.Invoke(this, _snapshot);
            return new StepResult(true, evt, _snapshot);
        }

        return new StepResult(false, null, _snapshot, "All Raft nodes are offline. Recover at least 3 nodes to elect a Leader.");
    }

    private void Initialize()
    {
        _rng = new Random(_seed);
        _queue = new PriorityQueue<RaftWork, (long TimeMs, long Seq)>();
        _currentTimeMs = 0;
        _nextQueueSeq = 0;
        _nextEventSeq = 0;
        _nextMsgNum = 0;
        _armedDelays.Clear();
        _armedDropCount = 0;
        _nodes.Clear();
        _nodeOrder.Clear();
        _inFlight.Clear();
        _eventLog.Clear();
        _leadersPerTerm.Clear();
        _messagesSent = 0;
        _messagesDropped = 0;
        _electionsStarted = 0;
        _leadersElected = 0;
        _lastActiveModuleId = null;

        // Spacious 5-node isometric pentagon
        var coords = new (double X, double Y)[]
        {
            (-1.2, -1.6),  // Node 1 (top-left)
            (2.4, -1.4),   // Node 2 (top-right)
            (3.4, 1.4),    // Node 3 (mid-right)
            (0.6, 3.2),    // Node 4 (bottom)
            (-2.0, 1.4)    // Node 5 (mid-left)
        };

        for (var i = 0; i < ServerCount; i++)
        {
            var id = $"node-{i + 1}";
            var name = $"Node {i + 1}";
            var node = new RaftNodeEntity(id, name, coords[i].X, coords[i].Y);
            _nodes[id] = node;
            _nodeOrder.Add(id);

            // Randomized election timeout per server (seeded deterministically)
            ScheduleNextElectionTimeout(node);
        }

        RebuildSnapshot();
    }

    private void Enqueue(long timeMs, RaftWork work)
    {
        work.TimeMs = timeMs;
        _queue.Enqueue(work, (timeMs, ++_nextQueueSeq));
    }

    private bool IsWorkStillValid(RaftWork work) => work switch
    {
        ElectionTimeoutWork t => _nodes.TryGetValue(t.NodeId, out var n) && !n.IsCrashed && n.Role != RaftRole.Leader && n.TimerToken == t.TimerToken,
        LeaderHeartbeatWork hb => _nodes.TryGetValue(hb.LeaderId, out var n) && !n.IsCrashed && n.Role == RaftRole.Leader && n.CurrentTerm == hb.Term && n.HeartbeatToken == hb.HeartbeatToken,
        _ => true
    };

    private void ScheduleNextElectionTimeout(RaftNodeEntity node)
    {
        var timeoutMs = _rng.Next(_electionTimeoutMinMs, _electionTimeoutMaxMs + 1);
        node.TimerToken++;
        node.NextTimeoutAtMs = _currentTimeMs + timeoutMs;
        Enqueue(node.NextTimeoutAtMs, new ElectionTimeoutWork(node.Id, node.TimerToken));
    }

    private EventRecord HandleElectionTimeout(ElectionTimeoutWork work)
    {
        var node = _nodes[work.NodeId];

        // Exact Raft Rule 1 & 2 (notes/02 §11.2):
        // Increment term, transition to Candidate, vote for self, reset timer, broadcast RequestVote
        node.CurrentTerm += 1;
        node.Role = RaftRole.Candidate;
        node.VotedFor = node.Id;
        node.KnownLeaderId = null;
        node.VotesReceived.Clear();
        node.VotesReceived.Add(node.Id);
        _electionsStarted++;

        ScheduleNextElectionTimeout(node);

        var evt = RecordEvent(
            SimulationEventType.CandidateStartedElection,
            EventCategory.Protocol,
            node.Id,
            null,
            $"{node.Id}-state",
            null,
            $"{node.Name} timed out → became Candidate for Term {node.CurrentTerm} (1/{QuorumSize} votes)",
            $"No Leader heartbeat arrived before {node.Name}'s election timer expired. It incremented Term to {node.CurrentTerm}, voted for itself, and sent RequestVote to all peers.",
            new Dictionary<string, string>
            {
                ["Candidate"] = node.Name,
                ["Term"] = node.CurrentTerm.ToString(),
                ["Votes"] = $"1 / {QuorumSize} needed"
            });

        foreach (var peerId in _nodeOrder.Where(id => id != node.Id))
        {
            SendRaftMessage(
                MessageKind.RequestVote,
                node.Id,
                peerId,
                node.CurrentTerm,
                $"Vote? T{node.CurrentTerm}",
                evt.Id);
        }

        return evt;
    }

    private EventRecord HandleLeaderHeartbeat(LeaderHeartbeatWork work)
    {
        var leader = _nodes[work.LeaderId];
        leader.HeartbeatsSent++;

        // Schedule next periodic heartbeat while still Leader of this term
        Enqueue(_currentTimeMs + _heartbeatIntervalMs, new LeaderHeartbeatWork(leader.Id, leader.CurrentTerm, leader.HeartbeatToken));

        var evt = RecordEvent(
            SimulationEventType.AppendEntriesSent,
            EventCategory.Heartbeats,
            leader.Id,
            null,
            $"{leader.Id}-rpc",
            null,
            $"Leader {leader.Name} sent Term {leader.CurrentTerm} heartbeats to followers",
            $"Leader {leader.Name} broadcasts periodic AppendEntries heartbeats for Term {leader.CurrentTerm} so followers reset their election timers and keep this term stable.",
            new Dictionary<string, string>
            {
                ["Leader"] = leader.Name,
                ["Term"] = leader.CurrentTerm.ToString()
            });

        foreach (var peerId in _nodeOrder.Where(id => id != leader.Id))
        {
            SendRaftMessage(
                MessageKind.AppendEntries,
                leader.Id,
                peerId,
                leader.CurrentTerm,
                $"HB · T{leader.CurrentTerm}",
                evt.Id);
        }

        return evt;
    }

    private EventRecord? HandleDeliverMessage(DeliverRaftMsgWork work)
    {
        if (!_inFlight.Remove(work.MessageId, out var msg))
        {
            return null;
        }

        if (work.WillDrop)
        {
            _messagesDropped++;
            return RecordEvent(
                SimulationEventType.MessageDropped,
                EventCategory.Faults,
                msg.SourceId,
                msg.TargetId,
                null,
                msg.CauseEventId,
                $"Network dropped {msg.Kind} ({msg.PillLabel}) from {_nodes[msg.SourceId].Name} → {_nodes[msg.TargetId].Name}",
                $"The simulated network dropped {msg.Kind} before it reached {_nodes[msg.TargetId].Name}. Raft handles lost RPCs through timeouts and retries.",
                new Dictionary<string, string>
                {
                    ["Kind"] = msg.Kind.ToString(),
                    ["Term"] = msg.Term.ToString()
                });
        }

        var dst = _nodes[msg.TargetId];
        var src = _nodes[msg.SourceId];

        if (dst.IsCrashed)
        {
            return null; // Crashed server silently ignores incoming packets
        }

        // Universal Raft Rule 5 (notes/02 §11.2):
        // Seeing a higher Term immediately updates currentTerm, clears votedFor, and steps down to Follower!
        var steppedDownFromLeader = false;
        if (msg.Term > dst.CurrentTerm)
        {
            steppedDownFromLeader = dst.Role == RaftRole.Leader;
            dst.CurrentTerm = msg.Term;
            dst.VotedFor = null;
            dst.VotesReceived.Clear();
            dst.Role = RaftRole.Follower;
            dst.HeartbeatToken++;
            ScheduleNextElectionTimeout(dst);
        }

        return msg.Kind switch
        {
            MessageKind.RequestVote => HandleRequestVoteReceived(dst, src, msg, steppedDownFromLeader),
            MessageKind.VoteGranted => HandleVoteGrantedReceived(dst, src, msg),
            MessageKind.VoteDenied => HandleVoteDeniedReceived(dst, src, msg, steppedDownFromLeader),
            MessageKind.AppendEntries => HandleAppendEntriesReceived(dst, src, msg, steppedDownFromLeader),
            _ => null
        };
    }

    private EventRecord HandleRequestVoteReceived(RaftNodeEntity dst, RaftNodeEntity src, RaftMessageEntity msg, bool steppedDownFromLeader)
    {
        var canGrant = msg.Term == dst.CurrentTerm &&
                       (dst.VotedFor is null || dst.VotedFor == src.Id);

        if (canGrant)
        {
            dst.VotedFor = src.Id;
            dst.VotesGrantedCount++;
            ScheduleNextElectionTimeout(dst);

            var evt = RecordEvent(
                SimulationEventType.VoteGranted,
                EventCategory.Protocol,
                dst.Id,
                src.Id,
                $"{dst.Id}-votes",
                msg.CauseEventId,
                steppedDownFromLeader
                    ? $"{dst.Name} stepped down to Term {dst.CurrentTerm} and voted for {src.Name}"
                    : $"{dst.Name} granted vote to {src.Name} for Term {msg.Term}",
                $"{dst.Name} had not voted for anyone else in Term {msg.Term}, so it recorded votedFor={src.Name}, reset its election timer, and replied VoteGranted.",
                new Dictionary<string, string>
                {
                    ["Voter"] = dst.Name,
                    ["Candidate"] = src.Name,
                    ["Term"] = msg.Term.ToString()
                });

            SendRaftMessage(MessageKind.VoteGranted, dst.Id, src.Id, dst.CurrentTerm, $"Vote OK · T{dst.CurrentTerm}", evt.Id);
            return evt;
        }
        else
        {
            var reason = msg.Term < dst.CurrentTerm
                ? $"request Term {msg.Term} is older than local Term {dst.CurrentTerm}"
                : $"already voted for {(_nodes.TryGetValue(dst.VotedFor ?? "", out var v) ? v.Name : dst.VotedFor)} in Term {dst.CurrentTerm}";

            var evt = RecordEvent(
                SimulationEventType.VoteDenied,
                EventCategory.Protocol,
                dst.Id,
                src.Id,
                $"{dst.Id}-votes",
                msg.CauseEventId,
                $"{dst.Name} denied vote to {src.Name} (Term {msg.Term})",
                $"{dst.Name} rejected RequestVote from {src.Name} because {reason}. Each server votes at most once per Term.",
                new Dictionary<string, string>
                {
                    ["Voter"] = dst.Name,
                    ["Candidate"] = src.Name,
                    ["LocalTerm"] = dst.CurrentTerm.ToString(),
                    ["Reason"] = reason
                });

            SendRaftMessage(MessageKind.VoteDenied, dst.Id, src.Id, dst.CurrentTerm, $"Deny · T{dst.CurrentTerm}", evt.Id);
            return evt;
        }
    }

    private EventRecord? HandleVoteGrantedReceived(RaftNodeEntity candidate, RaftNodeEntity voter, RaftMessageEntity msg)
    {
        if (candidate.Role != RaftRole.Candidate || msg.Term != candidate.CurrentTerm)
        {
            return null; // Ignore stale vote for an older term or after stepping down
        }

        candidate.VotesReceived.Add(voter.Id);

        if (candidate.VotesReceived.Count >= QuorumSize)
        {
            // Elected Leader for this Term!
            candidate.Role = RaftRole.Leader;
            candidate.KnownLeaderId = candidate.Id;
            candidate.TimerToken++; // Cancel election timer while Leader
            candidate.HeartbeatToken++;
            _leadersElected++;

            if (!_leadersPerTerm.TryGetValue(candidate.CurrentTerm, out var set))
            {
                set = [];
                _leadersPerTerm[candidate.CurrentTerm] = set;
            }
            set.Add(candidate.Id);

            // Start sending AppendEntries heartbeats immediately
            Enqueue(_currentTimeMs + 25, new LeaderHeartbeatWork(candidate.Id, candidate.CurrentTerm, candidate.HeartbeatToken));

            return RecordEvent(
                SimulationEventType.LeaderElected,
                EventCategory.Protocol,
                candidate.Id,
                null,
                $"{candidate.Id}-state",
                msg.CauseEventId,
                $"{candidate.Name} ELECTED LEADER for Term {candidate.CurrentTerm} ({candidate.VotesReceived.Count}/{ServerCount} votes!)",
                $"{candidate.Name} collected {candidate.VotesReceived.Count} votes (majority quorum is {QuorumSize} of {ServerCount}) for Term {candidate.CurrentTerm} and became Leader. Because any two majorities overlap, at most one Leader can win a given Term.",
                new Dictionary<string, string>
                {
                    ["Leader"] = candidate.Name,
                    ["Term"] = candidate.CurrentTerm.ToString(),
                    ["VoteCount"] = $"{candidate.VotesReceived.Count} / {ServerCount}"
                });
        }

        return RecordEvent(
            SimulationEventType.VoteRequested,
            EventCategory.Protocol,
            candidate.Id,
            voter.Id,
            $"{candidate.Id}-votes",
            msg.CauseEventId,
            $"{candidate.Name} received vote from {voter.Name} ({candidate.VotesReceived.Count}/{QuorumSize} needed)",
            $"{candidate.Name} now holds {candidate.VotesReceived.Count} votes in Term {candidate.CurrentTerm} and needs {QuorumSize} for a majority quorum.",
            new Dictionary<string, string>
            {
                ["Candidate"] = candidate.Name,
                ["Voter"] = voter.Name,
                ["Votes"] = $"{candidate.VotesReceived.Count} / {QuorumSize}"
            });
    }

    private EventRecord? HandleVoteDeniedReceived(RaftNodeEntity dst, RaftNodeEntity src, RaftMessageEntity msg, bool steppedDownFromLeader)
    {
        if (!steppedDownFromLeader)
        {
            return null;
        }

        return RecordEvent(
            SimulationEventType.LeaderSteppedDown,
            EventCategory.Protocol,
            dst.Id,
            src.Id,
            $"{dst.Id}-state",
            msg.CauseEventId,
            $"{dst.Name} saw higher Term {msg.Term} from {src.Name} and stepped down to Follower",
            $"Any Raft server that discovers a higher Term immediately updates its Term and steps down to Follower.",
            new Dictionary<string, string>
            {
                ["Node"] = dst.Name,
                ["NewTerm"] = dst.CurrentTerm.ToString()
            });
    }

    private EventRecord? HandleAppendEntriesReceived(RaftNodeEntity dst, RaftNodeEntity src, RaftMessageEntity msg, bool steppedDownFromLeader)
    {
        if (msg.Term < dst.CurrentTerm)
        {
            return null; // Ignore stale heartbeat from an old-term Leader
        }

        var wasCandidate = dst.Role == RaftRole.Candidate;
        dst.Role = RaftRole.Follower;
        dst.KnownLeaderId = src.Id;
        dst.HeartbeatsReceived++;
        ScheduleNextElectionTimeout(dst);

        if (steppedDownFromLeader || wasCandidate)
        {
            return RecordEvent(
                SimulationEventType.LeaderSteppedDown,
                EventCategory.Protocol,
                dst.Id,
                src.Id,
                $"{dst.Id}-state",
                msg.CauseEventId,
                $"{dst.Name} accepted Leader {src.Name} (Term {msg.Term}) and became Follower",
                $"{dst.Name} received a valid AppendEntries heartbeat from Term {msg.Term} Leader {src.Name}, stepped down to Follower, and reset its election timer.",
                new Dictionary<string, string>
                {
                    ["Follower"] = dst.Name,
                    ["Leader"] = src.Name,
                    ["Term"] = msg.Term.ToString()
                });
        }

        return RecordEvent(
            SimulationEventType.AppendEntriesReceived,
            EventCategory.Heartbeats,
            dst.Id,
            src.Id,
            $"{dst.Id}-timer",
            msg.CauseEventId,
            $"{dst.Name} received Leader {src.Name}'s heartbeat (Term {msg.Term}) and reset timer",
            $"{dst.Name} heard from Leader {src.Name} for Term {msg.Term}, keeping its Follower state and resetting its randomized election timeout.",
            new Dictionary<string, string>
            {
                ["Follower"] = dst.Name,
                ["Leader"] = src.Name,
                ["Term"] = msg.Term.ToString()
            });
    }

    private EventRecord? HandleCrashNode(CrashNodeWork work)
    {
        var node = _nodes[work.NodeId];
        if (node.IsCrashed)
        {
            return null;
        }

        var prevRole = node.Role;
        node.IsCrashed = true;
        node.TimerToken++;
        node.HeartbeatToken++;
        node.VotesReceived.Clear();

        return RecordEvent(
            SimulationEventType.WorkerCrashed,
            EventCategory.Faults,
            node.Id,
            null,
            $"{node.Id}-state",
            null,
            $"{node.Name} ({prevRole}, Term {node.CurrentTerm}) CRASHED",
            prevRole == RaftRole.Leader
                ? $"Leader {node.Name} stopped sending heartbeats. Followers will detect silence when their randomized election timers expire and start a new Term election."
                : $"{node.Name} went offline. Remember: Raft needs at least {QuorumSize} of {ServerCount} alive servers to form a majority quorum.",
            new Dictionary<string, string>
            {
                ["Node"] = node.Name,
                ["PreviousRole"] = prevRole.ToString(),
                ["Term"] = node.CurrentTerm.ToString(),
                ["AliveNodes"] = $"{_nodes.Values.Count(n => !n.IsCrashed)} / {ServerCount}"
            });
    }

    private EventRecord? HandleRecoverNode(RecoverNodeWork work)
    {
        var node = _nodes[work.NodeId];
        if (!node.IsCrashed)
        {
            return null;
        }

        // Durable Term & VotedFor are preserved across crash/recovery (notes/02 §11.1)
        node.IsCrashed = false;
        node.Role = RaftRole.Follower;
        node.VotesReceived.Clear();
        ScheduleNextElectionTimeout(node);

        return RecordEvent(
            SimulationEventType.WorkerRecovered,
            EventCategory.Faults,
            node.Id,
            null,
            $"{node.Id}-state",
            null,
            $"{node.Name} RECOVERED as Follower (durable Term {node.CurrentTerm})",
            $"{node.Name} restarted as a Follower while keeping its durable currentTerm ({node.CurrentTerm}) and votedFor state, and restarted its election timer.",
            new Dictionary<string, string>
            {
                ["Node"] = node.Name,
                ["RestoredTerm"] = node.CurrentTerm.ToString(),
                ["AliveNodes"] = $"{_nodes.Values.Count(n => !n.IsCrashed)} / {ServerCount}"
            });
    }

    private void SendRaftMessage(MessageKind kind, string sourceId, string targetId, int term, string pillLabel, string? causeEventId)
    {
        var msgId = $"rmsg-{++_nextMsgNum}";
        var isNonHb = kind != MessageKind.AppendEntries;
        var willDrop = false;
        var extraDelay = 0;

        if (isNonHb && _armedDropCount > 0)
        {
            _armedDropCount--;
            willDrop = true;
        }
        else if (isNonHb && _armedDelays.Count > 0)
        {
            extraDelay = _armedDelays.Dequeue();
        }

        var jitter = _rng.Next(-15, 25);
        var travelMs = Math.Max(35, _networkLatencyMs + jitter + extraDelay);
        var deliverAt = _currentTimeMs + travelMs;
        var isDelayed = extraDelay > 0;

        var msg = new RaftMessageEntity(
            msgId,
            kind,
            sourceId,
            targetId,
            term,
            _currentTimeMs,
            deliverAt,
            isDelayed ? $"{pillLabel} (Delayed)" : pillLabel,
            isDelayed,
            causeEventId);

        _inFlight[msgId] = msg;
        _messagesSent++;
        var deliveryTime = willDrop ? _currentTimeMs + Math.Max(20, travelMs / 2) : deliverAt;
        Enqueue(deliveryTime, new DeliverRaftMsgWork(msgId, willDrop));
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
        var nodes = _nodeOrder.Select(id => BuildRaftNodeSnapshot(_nodes[id])).ToList();
        var links = BuildRingAndActiveLinks();

        var inFlight = _inFlight.Values
            .OrderBy(m => m.SentAtMs)
            .Select(m => new MessageState(
                m.Id,
                m.Kind,
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

        var aliveCount = _nodes.Values.Count(n => !n.IsCrashed);
        var highestTerm = _nodes.Values.Max(n => n.CurrentTerm);
        var metrics = new SimulatedMetrics(
            SimulatedTimeMs: _currentTimeMs,
            QueueDepth: aliveCount,
            PendingTasks: _electionsStarted,
            RunningTasks: _inFlight.Count,
            CompletedTasks: _leadersElected,
            DispatchedTasks: highestTerm,
            RetryCount: _electionsStarted,
            MessagesSent: _messagesSent,
            MessagesDropped: _messagesDropped,
            ArmedDelayCount: _armedDelays.Count,
            ArmedDropCount: _armedDropCount);

        _snapshot = new SimulationState(
            ScenarioId: "raft-leader-election",
            ScenarioTitle: "Leader Election (Teaching Raft)",
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

    private NodeState BuildRaftNodeSnapshot(RaftNodeEntity n)
    {
        var health = n.IsCrashed
            ? NodeHealthStatus.Failed
            : n.Role == RaftRole.Leader
                ? NodeHealthStatus.Processing
                : n.Role == RaftRole.Candidate
                    ? NodeHealthStatus.Suspected
                    : NodeHealthStatus.Healthy;

        var statusSummary = n.IsCrashed
            ? $"Crashed · Term {n.CurrentTerm}"
            : n.Role == RaftRole.Leader
                ? $"LEADER · Term {n.CurrentTerm}"
                : n.Role == RaftRole.Candidate
                    ? $"Candidate · T{n.CurrentTerm} ({n.VotesReceived.Count}/{QuorumSize})"
                    : $"Follower · Term {n.CurrentTerm}";

        var votedName = n.VotedFor is null ? "None" : (_nodes.TryGetValue(n.VotedFor, out var v) ? v.Name : n.VotedFor);
        var remainingTimer = n.IsCrashed || n.Role == RaftRole.Leader
            ? "Paused"
            : $"{Math.Max(0, n.NextTimeoutAtMs - _currentTimeMs)} ms left";

        var modules = new List<ModuleState>
        {
            new(
                $"{n.Id}-state", n.Id, "Role & Term State", "Durable Term & Role",
                "Stores currentTerm, votedFor (persisted across restarts), and current Raft role (Follower, Candidate, or Leader).",
                n.IsCrashed ? ModuleStatus.Failed : (n.Role == RaftRole.Leader ? ModuleStatus.Processing : (n.Role == RaftRole.Candidate ? ModuleStatus.Suspected : ModuleStatus.Healthy)),
                0.0, 0.0,
                [$"Role: {(n.IsCrashed ? "CRASHED" : n.Role.ToString())}", $"Current Term: {n.CurrentTerm}", $"Voted For: {votedName}"],
                new Dictionary<string, string>
                {
                    ["Role"] = n.IsCrashed ? "Crashed" : n.Role.ToString(),
                    ["Current Term"] = n.CurrentTerm.ToString(),
                    ["Voted For (Durable)"] = votedName
                },
                n.IsCrashed ? "Crashed" : $"{n.Role} · T{n.CurrentTerm}"),
            new(
                $"{n.Id}-timer", n.Id, "Election Timer", "Randomized timeout",
                $"Waits {_electionTimeoutMinMs}–{_electionTimeoutMaxMs} ms for Leader heartbeats. Resets on valid AppendEntries or granting a vote.",
                n.IsCrashed ? ModuleStatus.Failed : (n.Role == RaftRole.Leader ? ModuleStatus.Idle : ModuleStatus.Healthy),
                2.9, 0.0,
                [n.Role == RaftRole.Leader ? "Leader mode (sends heartbeats)" : $"Timeout deadline: t={n.NextTimeoutAtMs} ms ({remainingTimer})"],
                new Dictionary<string, string>
                {
                    ["Timer State"] = remainingTimer,
                    ["Heartbeats Received"] = n.HeartbeatsReceived.ToString()
                }),
            new(
                $"{n.Id}-votes", n.Id, "Vote Manager", "Majority quorum tracker",
                $"Grants at most one vote per Term and tracks incoming VoteGranted replies until majority quorum ({QuorumSize} of {ServerCount}) is reached.",
                n.IsCrashed ? ModuleStatus.Failed : (n.Role == RaftRole.Candidate ? ModuleStatus.Processing : ModuleStatus.Idle),
                0.0, 2.8,
                n.Role == RaftRole.Candidate
                    ? [$"Term {n.CurrentTerm} votes: {n.VotesReceived.Count} / {QuorumSize} needed", $"Voters: {string.Join(", ", n.VotesReceived.Select(id => _nodes[id].Name))}"]
                    : [$"Last voted for: {votedName}", $"Total votes granted: {n.VotesGrantedCount}"],
                new Dictionary<string, string>
                {
                    ["Quorum Required"] = $"{QuorumSize} / {ServerCount}",
                    ["Current Votes"] = n.VotesReceived.Count.ToString()
                },
                n.Role == RaftRole.Candidate ? $"{n.VotesReceived.Count}/{QuorumSize} votes" : null),
            new(
                $"{n.Id}-rpc", n.Id, "RPC Transport", "RequestVote & Heartbeats",
                "Sends and receives RequestVote, VoteGranted/Denied, and AppendEntries heartbeat RPCs.",
                n.IsCrashed ? ModuleStatus.Failed : (_inFlight.Values.Any(m => m.SourceId == n.Id || m.TargetId == n.Id) ? ModuleStatus.Processing : ModuleStatus.Idle),
                2.9, 2.8,
                _inFlight.Values.Where(m => m.SourceId == n.Id).Select(m => $"Out: {m.PillLabel} → {_nodes[m.TargetId].Name}").Take(4).ToList(),
                new Dictionary<string, string>
                {
                    ["Heartbeats Sent"] = n.HeartbeatsSent.ToString()
                })
        };

        var links = new List<ModuleLink>
        {
            new($"{n.Id}-rl1", $"{n.Id}-timer", $"{n.Id}-state", "Timeout → Term++"),
            new($"{n.Id}-rl2", $"{n.Id}-state", $"{n.Id}-votes", "Check Quorum"),
            new($"{n.Id}-rl3", $"{n.Id}-votes", $"{n.Id}-rpc", "Vote RPCs", IsDashed: true),
            new($"{n.Id}-rl4", $"{n.Id}-rpc", $"{n.Id}-timer", "Reset on HB", IsDashed: true)
        };

        // Store RaftRole cleanly inside ActiveTaskCount/Capacity or RoleTag:
        // Role=RaftServer, Capacity=ServerCount, ActiveTaskCount=CurrentTerm!
        return new NodeState(
            n.Id,
            n.Name,
            NodeRole.RaftServer,
            health,
            QuorumSize,
            n.CurrentTerm,
            statusSummary,
            "A Raft voting server. Starts as Follower, becomes Candidate on election timeout, and leads once it earns a majority quorum (3 of 5).",
            n.GridX,
            n.GridY,
            modules,
            links,
            [$"Term {n.CurrentTerm} · VotedFor: {votedName}"],
            [n.Role == RaftRole.Leader ? $"Leading Term {n.CurrentTerm}" : $"Election timer: {remainingTimer}"]);
    }

    private List<LinkState> BuildRingAndActiveLinks()
    {
        var links = new List<LinkState>();
        var addedPairs = new HashSet<(string, string)>();

        void AddPair(string a, string b)
        {
            var key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            if (!addedPairs.Add(key))
            {
                return;
            }

            var active = _inFlight.Values.FirstOrDefault(m =>
                (m.SourceId == a && m.TargetId == b) || (m.SourceId == b && m.TargetId == a));

            var isVote = active is not null && active.Kind != MessageKind.AppendEntries;
            links.Add(new LinkState(
                $"raft-{key.Item1}-{key.Item2}",
                a,
                b,
                _networkLatencyMs,
                active?.PillLabel ?? string.Empty,
                IsHeartbeatChannel: !isVote,
                IsActive: active is not null));
        }

        // Always draw outer 5-node ring for clean visual topology
        for (var i = 0; i < _nodeOrder.Count; i++)
        {
            AddPair(_nodeOrder[i], _nodeOrder[(i + 1) % _nodeOrder.Count]);
        }

        // Also show active cross-ring RPC links whenever a Leader or Candidate is talking to non-adjacent peers
        foreach (var msg in _inFlight.Values)
        {
            AddPair(msg.SourceId, msg.TargetId);
        }

        return links;
    }

    private abstract class RaftWork
    {
        public long TimeMs { get; set; }
    }

    private sealed class ElectionTimeoutWork(string nodeId, int timerToken) : RaftWork
    {
        public string NodeId { get; } = nodeId;
        public int TimerToken { get; } = timerToken;
    }

    private sealed class LeaderHeartbeatWork(string leaderId, int term, int heartbeatToken) : RaftWork
    {
        public string LeaderId { get; } = leaderId;
        public int Term { get; } = term;
        public int HeartbeatToken { get; } = heartbeatToken;
    }

    private sealed class DeliverRaftMsgWork(string messageId, bool willDrop) : RaftWork
    {
        public string MessageId { get; } = messageId;
        public bool WillDrop { get; } = willDrop;
    }

    private sealed class CrashNodeWork(string nodeId) : RaftWork
    {
        public string NodeId { get; } = nodeId;
    }

    private sealed class RecoverNodeWork(string nodeId) : RaftWork
    {
        public string NodeId { get; } = nodeId;
    }

    private sealed class RaftNodeEntity(string id, string name, double gridX, double gridY)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public double GridX { get; } = gridX;
        public double GridY { get; } = gridY;
        public bool IsCrashed { get; set; }
        public RaftRole Role { get; set; } = RaftRole.Follower;
        public int CurrentTerm { get; set; }
        public string? VotedFor { get; set; }
        public string? KnownLeaderId { get; set; }
        public HashSet<string> VotesReceived { get; } = [];
        public int TimerToken { get; set; }
        public int HeartbeatToken { get; set; }
        public long NextTimeoutAtMs { get; set; }
        public int VotesGrantedCount { get; set; }
        public int HeartbeatsSent { get; set; }
        public int HeartbeatsReceived { get; set; }
    }

    private sealed class RaftMessageEntity(
        string id,
        MessageKind kind,
        string sourceId,
        string targetId,
        int term,
        long sentAtMs,
        long deliverAtMs,
        string pillLabel,
        bool isDelayed,
        string? causeEventId)
    {
        public string Id { get; } = id;
        public MessageKind Kind { get; } = kind;
        public string SourceId { get; } = sourceId;
        public string TargetId { get; } = targetId;
        public int Term { get; } = term;
        public long SentAtMs { get; } = sentAtMs;
        public long DeliverAtMs { get; } = deliverAtMs;
        public string PillLabel { get; } = pillLabel;
        public bool IsDelayed { get; } = isDelayed;
        public string? CauseEventId { get; } = causeEventId;
    }
}
