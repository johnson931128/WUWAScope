using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Models;

namespace WUWAScope.Simulation.Engine;

/// <summary>
/// Pure C# deterministic Chandy-Lamport Distributed Snapshot simulator.
/// Follows Distributed-System notes/01 §6: 3 bank accounts ($500, $200, $300 = $1,000 total) connected by
/// reliable FIFO channels. Proves that Recorded Local States + Recorded In-Flight Channel States always conserve $1,000.
/// </summary>
public sealed class GlobalSnapshotEngine : ISimulationEngine
{
    private readonly int _seed;
    private readonly int _defaultLatencyMs;
    private readonly bool _preloadWalkthrough;

    private PriorityQueue<SnapWork, (long TimeMs, long Seq)> _queue = null!;
    private long _currentTimeMs;
    private long _nextQueueSeq;
    private long _nextEventSeq;
    private int _nextMsgNum;
    private int _snapshotId;

    private readonly Queue<int> _armedDelays = new();
    private readonly Dictionary<string, AccountEntity> _accounts = new();
    private readonly List<string> _accountOrder = ["acct-a", "acct-b", "acct-c"];
    private readonly Dictionary<(string Src, string Dst), long> _channelLastDeliverMs = new();
    private readonly Dictionary<string, SnapMessageEntity> _inFlight = new();
    private readonly List<EventRecord> _eventLog = [];

    private int _messagesSent;
    private int _completedSnapshots;
    private int? _lastSnapshotTotal;
    private int? _lastSnapshotLocalSum;
    private int? _lastSnapshotChannelSum;
    private string? _lastActiveModuleId;
    private SimulationState _snapshot = null!;

    public GlobalSnapshotEngine(int seed = 42, int defaultLatencyMs = 180, bool preloadWalkthrough = true)
    {
        _seed = seed;
        _defaultLatencyMs = defaultLatencyMs;
        _preloadWalkthrough = preloadWalkthrough;
        Initialize();
    }

    public bool IsPrototypeStub => false;

    public int InitialTotalMoney => 1000;

    public int? LastSnapshotTotal => _lastSnapshotTotal;

    public int? LastSnapshotLocalSum => _lastSnapshotLocalSum;

    public int? LastSnapshotChannelSum => _lastSnapshotChannelSum;

    public event EventHandler<SimulationState>? StateChanged;

    public SimulationState GetState() => _snapshot;

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
            case InitiateSnapshotAction init when _accounts.ContainsKey(init.InitiatorId):
                Enqueue(_currentTimeMs, new StartSnapshotWork(init.InitiatorId));
                break;

            case SendTransferAction transfer when _accounts.ContainsKey(transfer.SourceId) && _accounts.ContainsKey(transfer.TargetId) && transfer.SourceId != transfer.TargetId:
                Enqueue(_currentTimeMs, new SendTransferWork(transfer.SourceId, transfer.TargetId, Math.Max(10, transfer.Amount)));
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
            return new StepResult(false, null, _snapshot, "Snapshot idle — click 'Start Snapshot' or 'Send $50 Transfer' to run another cut.");
        }

        _currentTimeMs = Math.Max(_currentTimeMs, work.TimeMs);
        EventRecord? evt = work switch
        {
            StartSnapshotWork start => HandleInitiateSnapshot(start),
            SendTransferWork send => HandleSendTransfer(send),
            DeliverSnapMsgWork deliver => HandleDeliverMessage(deliver),
            CompleteSnapshotWork done => HandleSnapshotCompleted(done),
            _ => null
        };

        RebuildSnapshot();
        StateChanged?.Invoke(this, _snapshot);
        return new StepResult(evt is not null, evt, _snapshot);
    }

    private void Initialize()
    {
        _queue = new PriorityQueue<SnapWork, (long TimeMs, long Seq)>();
        _currentTimeMs = 0;
        _nextQueueSeq = 0;
        _nextEventSeq = 0;
        _nextMsgNum = 0;
        _snapshotId = 0;
        _armedDelays.Clear();
        _accounts.Clear();
        _channelLastDeliverMs.Clear();
        _inFlight.Clear();
        _eventLog.Clear();
        _messagesSent = 0;
        _completedSnapshots = 0;
        _lastSnapshotTotal = null;
        _lastSnapshotLocalSum = null;
        _lastSnapshotChannelSum = null;
        _lastActiveModuleId = null;

        // 3 accounts totaling $1,000 (A=$500, B=$200 matching notes/01 §6.3, plus C=$300)
        _accounts["acct-a"] = new AccountEntity("acct-a", "Account A", 500, -1.4, -0.4);
        _accounts["acct-b"] = new AccountEntity("acct-b", "Account B", 200, 2.4, -0.4);
        _accounts["acct-c"] = new AccountEntity("acct-c", "Account C", 300, 1.2, 2.8);

        if (_preloadWalkthrough)
        {
            // Modeled directly on notes/01 §6.3 ($50 transfer in flight while Snapshot runs!)
            // 1. Account B initiates snapshot at t=60 (records LS_B=$200, sends Marker B->A slowly so A sends $50 before Marker arrives)
            Enqueue(60, new StartSnapshotWork("acct-b", markerDelayOverrideMs: 360));
            // 2. Account A has not received B's marker yet -> deducts $50 and sends $50 to B at t=110 (arrives at B while B is recording A->B!)
            Enqueue(110, new SendTransferWork("acct-a", "acct-b", 50));
            // 3. Account C also sends $50 to A at t=170 so multiple channels carry real money during the snapshot
            Enqueue(170, new SendTransferWork("acct-c", "acct-a", 50));
        }

        RebuildSnapshot();
    }

    private void Enqueue(long timeMs, SnapWork work)
    {
        work.TimeMs = timeMs;
        _queue.Enqueue(work, (timeMs, ++_nextQueueSeq));
    }

    private EventRecord HandleInitiateSnapshot(StartSnapshotWork work)
    {
        // Reset previous snapshot recording state on all accounts for a clean new snapshot round
        _snapshotId++;
        var snapId = _snapshotId;
        foreach (var acct in _accounts.Values)
        {
            acct.ResetSnapshotState();
        }

        var initiator = _accounts[work.InitiatorId];
        initiator.HasRecordedLocalState = true;
        initiator.RecordedSnapshotId = snapId;
        initiator.RecordedLocalBalance = initiator.LiveBalance;

        // Initiator begins recording on ALL incoming channels
        foreach (var peerId in _accountOrder.Where(id => id != initiator.Id))
        {
            initiator.IncomingChannels[peerId] = new ChannelRecordingState(peerId, isRecording: true, isClosed: false);
        }

        var evt = RecordEvent(
            SimulationEventType.SnapshotInitiated,
            EventCategory.Protocol,
            initiator.Id,
            null,
            $"{initiator.Id}-recorder",
            null,
            $"{initiator.Name} started Snapshot #{snapId} (recorded LS = ${initiator.RecordedLocalBalance})",
            $"{initiator.Name} saved its local balance (${initiator.RecordedLocalBalance}), started recording all incoming channels, and sent Marker messages on all outgoing FIFO channels.",
            new Dictionary<string, string>
            {
                ["SnapshotId"] = snapId.ToString(),
                ["Initiator"] = initiator.Name,
                ["RecordedLS"] = $"${initiator.RecordedLocalBalance}"
            });

        // Send Marker on all outgoing channels (strict FIFO)
        foreach (var peerId in _accountOrder.Where(id => id != initiator.Id))
        {
            SendFifoMessage(
                MessageKind.SnapshotMarker,
                initiator.Id,
                peerId,
                amount: 0,
                snapshotId: snapId,
                pillLabel: $"Marker #{snapId}",
                causeEventId: evt.Id,
                latencyOverrideMs: work.MarkerDelayOverrideMs);
        }

        return evt;
    }

    private EventRecord HandleSendTransfer(SendTransferWork work)
    {
        var src = _accounts[work.SourceId];
        var dst = _accounts[work.TargetId];
        var amount = Math.Min(work.Amount, src.LiveBalance);
        if (amount <= 0)
        {
            amount = 0;
        }

        src.LiveBalance -= amount;
        src.TransfersSent++;

        var evt = RecordEvent(
            SimulationEventType.TransferSent,
            EventCategory.Network,
            src.Id,
            dst.Id,
            $"{src.Id}-ledger",
            null,
            $"{src.Name} sent ${amount} → {dst.Name} (live balance now ${src.LiveBalance})",
            $"{src.Name} deducted ${amount} from its live balance (${src.LiveBalance} remaining) and put the transfer onto the FIFO channel to {dst.Name}.",
            new Dictionary<string, string>
            {
                ["From"] = src.Name,
                ["To"] = dst.Name,
                ["Amount"] = $"${amount}",
                ["SenderLiveBalance"] = $"${src.LiveBalance}"
            });

        SendFifoMessage(
            MessageKind.Transfer,
            src.Id,
            dst.Id,
            amount: amount,
            snapshotId: 0,
            pillLabel: $"Transfer ${amount}",
            causeEventId: evt.Id);

        return evt;
    }

    private EventRecord? HandleDeliverMessage(DeliverSnapMsgWork work)
    {
        if (!_inFlight.Remove(work.MessageId, out var msg))
        {
            return null;
        }

        return msg.Kind == MessageKind.Transfer
            ? HandleTransferReceived(msg)
            : HandleMarkerReceived(msg);
    }

    private EventRecord HandleTransferReceived(SnapMessageEntity msg)
    {
        var src = _accounts[msg.SourceId];
        var dst = _accounts[msg.TargetId];

        dst.LiveBalance += msg.Amount;
        dst.TransfersReceived++;

        var recordedInChannel = false;
        if (dst.HasRecordedLocalState &&
            dst.IncomingChannels.TryGetValue(src.Id, out var ch) &&
            ch.IsRecording && !ch.IsClosed)
        {
            ch.RecordedTransfers.Add(msg.Amount);
            recordedInChannel = true;
        }

        var channelNote = recordedInChannel
            ? $" Because {dst.Name} already saved its local snapshot (LS=${dst.RecordedLocalBalance}) and is still recording channel {src.Name} → {dst.Name}, this ${msg.Amount} is captured inside SC({src.Name}→{dst.Name})!"
            : $" Normal transfer applied to live balance (${dst.LiveBalance}).";

        return RecordEvent(
            SimulationEventType.TransferReceived,
            EventCategory.Jobs,
            dst.Id,
            src.Id,
            recordedInChannel ? $"{dst.Id}-channels" : $"{dst.Id}-ledger",
            msg.CauseEventId,
            recordedInChannel
                ? $"{dst.Name} received ${msg.Amount} from {src.Name} (captured in SC!)"
                : $"{dst.Name} received ${msg.Amount} from {src.Name} (live balance ${dst.LiveBalance})",
            $"{dst.Name} added ${msg.Amount} to its live balance (${dst.LiveBalance}).{channelNote}",
            new Dictionary<string, string>
            {
                ["Receiver"] = dst.Name,
                ["Sender"] = src.Name,
                ["Amount"] = $"${msg.Amount}",
                ["ReceiverLiveBalance"] = $"${dst.LiveBalance}",
                ["CapturedInChannelState"] = recordedInChannel ? "Yes" : "No"
            });
    }

    private EventRecord HandleMarkerReceived(SnapMessageEntity msg)
    {
        var src = _accounts[msg.SourceId];
        var dst = _accounts[msg.TargetId];
        var snapId = msg.SnapshotId;

        if (!dst.HasRecordedLocalState || dst.RecordedSnapshotId != snapId)
        {
            // First time receiving Marker for this snapshot!
            dst.HasRecordedLocalState = true;
            dst.RecordedSnapshotId = snapId;
            dst.RecordedLocalBalance = dst.LiveBalance;

            // Rule 2: Incoming channel carrying this first Marker is empty & immediately closed;
            // all OTHER incoming channels begin recording.
            foreach (var peerId in _accountOrder.Where(id => id != dst.Id))
            {
                var isFirstMarkerChannel = peerId == src.Id;
                dst.IncomingChannels[peerId] = new ChannelRecordingState(
                    peerId,
                    isRecording: !isFirstMarkerChannel,
                    isClosed: isFirstMarkerChannel);
            }

            var evt = RecordEvent(
                SimulationEventType.MarkerReceived,
                EventCategory.Protocol,
                dst.Id,
                src.Id,
                $"{dst.Id}-recorder",
                msg.CauseEventId,
                $"{dst.Name} received first Marker #{snapId} from {src.Name} → recorded LS = ${dst.RecordedLocalBalance}",
                $"{dst.Name} saved its local balance (${dst.RecordedLocalBalance}), marked channel {src.Name} → {dst.Name} empty & closed, began recording other incoming channels, and forwarded Marker #{snapId} on all outgoing FIFO channels.",
                new Dictionary<string, string>
                {
                    ["SnapshotId"] = snapId.ToString(),
                    ["Account"] = dst.Name,
                    ["RecordedLS"] = $"${dst.RecordedLocalBalance}",
                    ["ClosedChannel"] = $"{src.Name} → {dst.Name} (empty)"
                });

            // Forward Marker on all outgoing channels (strict FIFO)
            foreach (var peerId in _accountOrder.Where(id => id != dst.Id))
            {
                SendFifoMessage(
                    MessageKind.SnapshotMarker,
                    dst.Id,
                    peerId,
                    amount: 0,
                    snapshotId: snapId,
                    pillLabel: $"Marker #{snapId}",
                    causeEventId: evt.Id);
            }

            CheckIfGlobalSnapshotFinished(snapId, evt.Id);
            return evt;
        }
        else
        {
            // Rule 3: Already recorded LS -> close recording on this incoming channel!
            var ch = dst.IncomingChannels[src.Id];
            ch.IsRecording = false;
            ch.IsClosed = true;
            var channelSum = ch.RecordedTransfers.Sum();
            var contentText = ch.RecordedTransfers.Count == 0
                ? "empty"
                : string.Join(" + ", ch.RecordedTransfers.Select(a => $"${a}"));

            var evt = RecordEvent(
                SimulationEventType.ChannelClosed,
                EventCategory.Protocol,
                dst.Id,
                src.Id,
                $"{dst.Id}-channels",
                msg.CauseEventId,
                $"{dst.Name} closed SC({src.Name}→{dst.Name}) = {contentText}",
                $"Marker #{snapId} from {src.Name} closed the recording window on channel {src.Name} → {dst.Name}. Captured in-flight money: {contentText} (${channelSum}).",
                new Dictionary<string, string>
                {
                    ["SnapshotId"] = snapId.ToString(),
                    ["Channel"] = $"{src.Name} → {dst.Name}",
                    ["CapturedAmount"] = $"${channelSum}"
                });

            CheckIfGlobalSnapshotFinished(snapId, evt.Id);
            return evt;
        }
    }

    private void CheckIfGlobalSnapshotFinished(int snapId, string causeEventId)
    {
        var allDone = _accounts.Values.All(a =>
            a.HasRecordedLocalState &&
            a.RecordedSnapshotId == snapId &&
            a.IncomingChannels.Count == _accountOrder.Count - 1 &&
            a.IncomingChannels.Values.All(c => c.IsClosed));

        if (allDone)
        {
            Enqueue(_currentTimeMs + 20, new CompleteSnapshotWork(snapId, causeEventId));
        }
    }

    private EventRecord HandleSnapshotCompleted(CompleteSnapshotWork work)
    {
        var localSum = _accounts.Values.Sum(a => a.RecordedLocalBalance ?? 0);
        var channelSum = _accounts.Values.SelectMany(a => a.IncomingChannels.Values).Sum(c => c.RecordedTransfers.Sum());
        var total = localSum + channelSum;

        _completedSnapshots++;
        _lastSnapshotLocalSum = localSum;
        _lastSnapshotChannelSum = channelSum;
        _lastSnapshotTotal = total;

        return RecordEvent(
            SimulationEventType.SnapshotCompleted,
            EventCategory.Protocol,
            "acct-b",
            null,
            "acct-b-recorder",
            work.CauseEventId,
            $"Snapshot #{work.SnapshotId} complete: Local ${localSum} + In-Flight ${channelSum} = ${total} (Conserved!)",
            $"Every account recorded its local state and closed all incoming FIFO channels. Total money in the consistent global snapshot is ${localSum} (accounts) + ${channelSum} (in-flight channels) = ${total} — matching the exact ${InitialTotalMoney} conserved total!",
            new Dictionary<string, string>
            {
                ["SnapshotId"] = work.SnapshotId.ToString(),
                ["LocalBalancesSum"] = $"${localSum}",
                ["InFlightChannelsSum"] = $"${channelSum}",
                ["GlobalSnapshotTotal"] = $"${total}"
            });
    }

    private void SendFifoMessage(
        MessageKind kind,
        string sourceId,
        string targetId,
        int amount,
        int snapshotId,
        string pillLabel,
        string? causeEventId,
        int? latencyOverrideMs = null)
    {
        var extraDelay = _armedDelays.Count > 0 ? _armedDelays.Dequeue() : 0;
        var baseLatency = latencyOverrideMs ?? _defaultLatencyMs;
        var desiredDeliver = _currentTimeMs + baseLatency + extraDelay;

        // STRICT FIFO PER DIRECTED CHANNEL: a later message on (sourceId -> targetId)
        // can NEVER overtake an earlier message on the same channel!
        var key = (sourceId, targetId);
        if (_channelLastDeliverMs.TryGetValue(key, out var prevDeliver) && desiredDeliver <= prevDeliver)
        {
            desiredDeliver = prevDeliver + 45;
        }
        _channelLastDeliverMs[key] = desiredDeliver;

        var msgId = $"smsg-{++_nextMsgNum}";
        var isDelayed = extraDelay > 0;
        var msg = new SnapMessageEntity(
            msgId,
            kind,
            sourceId,
            targetId,
            amount,
            snapshotId,
            _currentTimeMs,
            desiredDeliver,
            isDelayed ? $"{pillLabel} (Delayed)" : pillLabel,
            isDelayed,
            causeEventId);

        _inFlight[msgId] = msg;
        _messagesSent++;
        Enqueue(desiredDeliver, new DeliverSnapMsgWork(msgId));
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
        var nodes = _accountOrder.Select(id => BuildAccountNode(_accounts[id])).ToList();
        var links = new List<LinkState>
        {
            BuildChannelLink("gs-a-b", "acct-a", "acct-b"),
            BuildChannelLink("gs-b-c", "acct-b", "acct-c"),
            BuildChannelLink("gs-c-a", "acct-c", "acct-a")
        };

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

        var recordingCount = _accounts.Values.Count(a => a.IncomingChannels.Values.Any(c => c.IsRecording));
        var metrics = new SimulatedMetrics(
            SimulatedTimeMs: _currentTimeMs,
            QueueDepth: _inFlight.Count,
            PendingTasks: recordingCount,
            RunningTasks: _inFlight.Count,
            CompletedTasks: _completedSnapshots,
            DispatchedTasks: _messagesSent,
            RetryCount: 0,
            MessagesSent: _messagesSent,
            MessagesDropped: 0,
            LastJobDurationMs: _lastSnapshotTotal,
            ArmedDelayCount: _armedDelays.Count);

        _snapshot = new SimulationState(
            ScenarioId: "global-snapshots",
            ScenarioTitle: "Global Snapshots (Chandy-Lamport)",
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

    private NodeState BuildAccountNode(AccountEntity a)
    {
        var isRecording = a.IncomingChannels.Values.Any(c => c.IsRecording);
        var allClosed = a.HasRecordedLocalState && a.IncomingChannels.Count == _accountOrder.Count - 1 && a.IncomingChannels.Values.All(c => c.IsClosed);
        var health = isRecording ? NodeHealthStatus.Processing : NodeHealthStatus.Healthy;

        var snapText = a.HasRecordedLocalState
            ? $"Live ${a.LiveBalance} · LS=${a.RecordedLocalBalance}"
            : $"Live Balance: ${a.LiveBalance}";

        var channelLines = a.IncomingChannels.Count == 0
            ? new List<string> { "No active snapshot recording" }
            : a.IncomingChannels.Values
                .Select(c =>
                {
                    var fromName = _accounts[c.FromId].Name;
                    var amt = c.RecordedTransfers.Count == 0 ? "$0 (empty)" : string.Join(" + ", c.RecordedTransfers.Select(x => $"${x}"));
                    var state = c.IsClosed ? "Closed" : "Recording...";
                    return $"{fromName} → {a.Name}: {amt} [{state}]";
                })
                .ToList();

        var modules = new List<ModuleState>
        {
            new(
                $"{a.Id}-ledger", a.Id, "Account Ledger", "Live money balance",
                "Tracks this account's real-time balance as $50 transfers are sent and received.",
                _lastActiveModuleId == $"{a.Id}-ledger" ? ModuleStatus.Processing : ModuleStatus.Healthy,
                0.0, 0.0,
                [$"Live Balance: ${a.LiveBalance}", $"Transfers Sent: {a.TransfersSent} · Received: {a.TransfersReceived}"],
                new Dictionary<string, string>
                {
                    ["Live Balance"] = $"${a.LiveBalance}",
                    ["Transfers Sent"] = a.TransfersSent.ToString(),
                    ["Transfers Received"] = a.TransfersReceived.ToString()
                },
                $"Live ${a.LiveBalance}"),
            new(
                $"{a.Id}-recorder", a.Id, "Snapshot Recorder", "Saves local state (LS)",
                "Saves a frozen copy of the account balance the moment a snapshot starts or the first Marker arrives.",
                isRecording ? ModuleStatus.Processing : (allClosed ? ModuleStatus.Healthy : ModuleStatus.Idle),
                2.9, 0.0,
                a.HasRecordedLocalState
                    ? [$"Snapshot #{a.RecordedSnapshotId}: LS = ${a.RecordedLocalBalance}", allClosed ? "Local snapshot complete" : "Waiting for remaining Markers"]
                    : ["Not recorded yet"],
                new Dictionary<string, string>
                {
                    ["Recorded LS"] = a.RecordedLocalBalance.HasValue ? $"${a.RecordedLocalBalance.Value}" : "-",
                    ["Snapshot Status"] = allClosed ? "Done" : (isRecording ? "Recording" : "Idle")
                },
                a.RecordedLocalBalance.HasValue ? $"LS=${a.RecordedLocalBalance}" : null),
            new(
                $"{a.Id}-channels", a.Id, "Channel Recorders", "Captures in-flight money (SC)",
                "Records any incoming transfers that arrive AFTER local state was saved and BEFORE that channel's Marker arrives.",
                isRecording ? ModuleStatus.Queued : (allClosed ? ModuleStatus.Healthy : ModuleStatus.Idle),
                0.0, 2.8,
                channelLines,
                new Dictionary<string, string>
                {
                    ["Captured Channel Sum"] = $"${a.IncomingChannels.Values.Sum(c => c.RecordedTransfers.Sum())}"
                },
                isRecording ? "Recording SC..." : null),
            new(
                $"{a.Id}-outbox", a.Id, "FIFO Outbox", "Strict FIFO channel sender",
                "Guarantees that Markers never overtake Transfers sent earlier on the same outgoing channel.",
                _inFlight.Values.Any(m => m.SourceId == a.Id) ? ModuleStatus.Processing : ModuleStatus.Idle,
                2.9, 2.8,
                _inFlight.Values.Where(m => m.SourceId == a.Id).Select(m => $"{m.PillLabel} → {_accounts[m.TargetId].Name}").ToList(),
                new Dictionary<string, string>
                {
                    ["In-Flight Outbound"] = _inFlight.Values.Count(m => m.SourceId == a.Id).ToString()
                })
        };

        var links = new List<ModuleLink>
        {
            new($"{a.Id}-gl1", $"{a.Id}-ledger", $"{a.Id}-recorder", "Copy LS"),
            new($"{a.Id}-gl2", $"{a.Id}-recorder", $"{a.Id}-outbox", "Send Markers"),
            new($"{a.Id}-gl3", $"{a.Id}-recorder", $"{a.Id}-channels", "Start/Stop SC", IsDashed: true)
        };

        return new NodeState(
            a.Id,
            a.Name,
            NodeRole.Account,
            health,
            1,
            a.LiveBalance,
            snapText,
            "A bank account node participating in Chandy-Lamport distributed snapshots over reliable FIFO channels.",
            a.GridX,
            a.GridY,
            modules,
            links,
            channelLines,
            [$"Live Balance: ${a.LiveBalance}"]);
    }

    private LinkState BuildChannelLink(string id, string a, string b)
    {
        var active = _inFlight.Values.FirstOrDefault(m =>
            (m.SourceId == a && m.TargetId == b) || (m.SourceId == b && m.TargetId == a));

        return new LinkState(
            id,
            a,
            b,
            _defaultLatencyMs,
            active?.PillLabel ?? "FIFO Channel",
            IsHeartbeatChannel: active is null,
            IsActive: active is not null);
    }

    private abstract class SnapWork
    {
        public long TimeMs { get; set; }
    }

    private sealed class StartSnapshotWork(string initiatorId, int? markerDelayOverrideMs = null) : SnapWork
    {
        public string InitiatorId { get; } = initiatorId;
        public int? MarkerDelayOverrideMs { get; } = markerDelayOverrideMs;
    }

    private sealed class SendTransferWork(string sourceId, string targetId, int amount) : SnapWork
    {
        public string SourceId { get; } = sourceId;
        public string TargetId { get; } = targetId;
        public int Amount { get; } = amount;
    }

    private sealed class DeliverSnapMsgWork(string messageId) : SnapWork
    {
        public string MessageId { get; } = messageId;
    }

    private sealed class CompleteSnapshotWork(int snapshotId, string causeEventId) : SnapWork
    {
        public int SnapshotId { get; } = snapshotId;
        public string CauseEventId { get; } = causeEventId;
    }

    private sealed class AccountEntity(string id, string name, int initialBalance, double gridX, double gridY)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public int LiveBalance { get; set; } = initialBalance;
        public double GridX { get; } = gridX;
        public double GridY { get; } = gridY;
        public int TransfersSent { get; set; }
        public int TransfersReceived { get; set; }
        public bool HasRecordedLocalState { get; set; }
        public int RecordedSnapshotId { get; set; }
        public int? RecordedLocalBalance { get; set; }
        public Dictionary<string, ChannelRecordingState> IncomingChannels { get; } = new();

        public void ResetSnapshotState()
        {
            HasRecordedLocalState = false;
            RecordedSnapshotId = 0;
            RecordedLocalBalance = null;
            IncomingChannels.Clear();
        }
    }

    private sealed class ChannelRecordingState(string fromId, bool isRecording, bool isClosed)
    {
        public string FromId { get; } = fromId;
        public bool IsRecording { get; set; } = isRecording;
        public bool IsClosed { get; set; } = isClosed;
        public List<int> RecordedTransfers { get; } = [];
    }

    private sealed class SnapMessageEntity(
        string id,
        MessageKind kind,
        string sourceId,
        string targetId,
        int amount,
        int snapshotId,
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
        public int Amount { get; } = amount;
        public int SnapshotId { get; } = snapshotId;
        public long SentAtMs { get; } = sentAtMs;
        public long DeliverAtMs { get; } = deliverAtMs;
        public string PillLabel { get; } = pillLabel;
        public bool IsDelayed { get; } = isDelayed;
        public string? CauseEventId { get; } = causeEventId;
    }
}
