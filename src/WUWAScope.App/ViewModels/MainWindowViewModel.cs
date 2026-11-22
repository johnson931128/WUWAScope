using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WUWAScope.Simulation.Contracts;
using WUWAScope.Simulation.Engine;
using WUWAScope.Simulation.Models;
using WUWAScope.Simulation.Stubs;

namespace WUWAScope.App.ViewModels;

public enum NavigationLevel
{
    Cluster,
    Node,
    Module
}

public sealed record KeyValueItem(string Label, string Value);

public sealed record ScenarioListItem(
    ScenarioDescriptor Descriptor,
    bool IsSelected);

public sealed record ModuleListItem(
    string Id,
    string NodeId,
    string Name,
    string ShortRole,
    string StatusBadge,
    bool IsSelected);

public sealed record NodeOutlineItem(
    string Id,
    string Name,
    NodeRole Role,
    string RoleGlyph,
    string RoleTag,
    string StatusBadge,
    bool IsSelected,
    bool IsActiveInCanvas,
    bool IsExpanded,
    string ChevronIcon,
    IReadOnlyList<ModuleListItem> Modules);

public sealed record EventListItem(
    EventRecord Record,
    string TimeDisplay,
    string RouteDisplay,
    string CategoryDisplay,
    string FriendlyTag,
    string DotColorHex,
    bool IsSelected);

public partial class MainWindowViewModel : ViewModelBase
{
    private const double BaseSimMsPerRealSecond = 420.0;

    private ISimulationEngine _engine;
    private readonly HashSet<string> _expandedNodeIds = [];
    private bool _suppressScenarioChange;

    public event Action? LayoutResetRequested;

    [ObservableProperty]
    private SimulationState _currentState;

    [ObservableProperty]
    private ScenarioDescriptor _activeScenario;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _visualTimeMs;

    [ObservableProperty]
    private NavigationLevel _currentLevel = NavigationLevel.Cluster;

    [ObservableProperty]
    private string? _activeNodeId;

    [ObservableProperty]
    private string? _selectedNodeId;

    [ObservableProperty]
    private string? _selectedModuleId;

    [ObservableProperty]
    private string? _selectedEventId;

    [ObservableProperty]
    private bool _isLeftSidebarOpen = true;

    [ObservableProperty]
    private bool _isRightInspectorOpen = true;

    [ObservableProperty]
    private bool _isBottomTimelineOpen = true;

    [ObservableProperty]
    private int _selectedInspectorTabIndex;

    [ObservableProperty]
    private bool _hideHeartbeats = true;

    [ObservableProperty]
    private string _eventCategoryFilter = "All";

    [ObservableProperty]
    private string _eventNodeFilter = "All";

    [ObservableProperty]
    private string _eventSearchText = string.Empty;

    [ObservableProperty]
    private string _selectedSpeedLabel = "1.0x";

    [ObservableProperty]
    private string _statusBannerText = string.Empty;

    // Inspector display state
    [ObservableProperty]
    private string _inspectorKindLabel = "CLUSTER";

    [ObservableProperty]
    private string _inspectorTitle = "Distributed Task Scheduling";

    [ObservableProperty]
    private string _inspectorSubtitle = string.Empty;

    [ObservableProperty]
    private string _inspectorStatusLabel = "● Healthy";

    [ObservableProperty]
    private string _inspectorPlainDescription = string.Empty;

    [ObservableProperty]
    private string _inspectorQueueTitle = "Live Activity";

    [ObservableProperty]
    private EventRecord? _selectedEvent;

    public MainWindowViewModel(ISimulationEngine engine)
    {
        _engine = engine;
        _currentState = _engine.GetState();
        _visualTimeMs = _currentState.SimulatedTimeMs;
        _engine.StateChanged += OnEngineStateChanged;

        Scenarios = ScenarioCatalog.All;
        _activeScenario = Scenarios.FirstOrDefault(s => s.Id == _currentState.ScenarioId) ?? Scenarios[0];
        SpeedOptions = ["0.5x", "1.0x", "2.0x", "4.0x"];
        EventCategoryOptions = ["All", "Protocol", "Jobs", "Network", "Heartbeats", "Faults"];

        InitializeSelectionForCurrentScenario();
        RefreshAllDerivedCollections();
    }

    public IReadOnlyList<ScenarioDescriptor> Scenarios { get; }

    public IReadOnlyList<string> SpeedOptions { get; }

    public IReadOnlyList<string> EventCategoryOptions { get; }

    public ObservableCollection<ScenarioListItem> ScenarioItems { get; } = [];

    public ObservableCollection<string> EventNodeOptions { get; } = [];

    public ObservableCollection<NodeOutlineItem> ClusterNodes { get; } = [];

    public ObservableCollection<string> InspectorQueueItems { get; } = [];

    public ObservableCollection<KeyValueItem> InspectorCounters { get; } = [];

    public ObservableCollection<KeyValueItem> InspectorQuickStats { get; } = [];

    public ObservableCollection<ModuleListItem> InspectorChildModules { get; } = [];

    public ObservableCollection<EventListItem> FilteredEvents { get; } = [];

    public ObservableCollection<KeyValueItem> SelectedEventPayload { get; } = [];

    public bool IsPrototypeStub => _engine.IsPrototypeStub;

    public bool CanRunLivePlayback => !_engine.IsPrototypeStub;

    public bool CanPlay => CanRunLivePlayback && !IsPlaying;

    public bool CanPause => CanRunLivePlayback && IsPlaying;

    public string PlayPauseLabel => IsPlaying ? "⏸ Pause" : "▶ Play";

    public double SpeedMultiplier => SelectedSpeedLabel switch
    {
        "0.5x" => 0.5,
        "2.0x" => 2.0,
        "4.0x" => 4.0,
        _ => 1.0
    };

    public string SimulatedTimeDisplay => $"{Math.Round(VisualTimeMs):0} ms";

    public string HeartbeatToggleLabel => HideHeartbeats ? "♡ Heartbeats: Off" : "♥ Heartbeats: On";

    public bool IsAllCategoryActive => EventCategoryFilter == "All";

    public bool IsFaultsCategoryActive => EventCategoryFilter == "Faults";

    public bool IsProtocolCategoryActive => EventCategoryFilter == "Protocol";

    public bool HasFilteredEvents => FilteredEvents.Count > 0;

    public string EmptyFlowHint => CurrentState.EventLog.Count == 0
        ? "Press Play (Space) or Step (N) to watch packets move."
        : HideHeartbeats
            ? "Only background heartbeats occurred so far. Keep stepping or turn on Heartbeats."
            : "No events match your current filter.";

    // Backwards-compatible properties for Task Scheduling + dynamic labels for all 4 scenarios
    public string TargetWorkerActionLabel
    {
        get
        {
            var target = ResolveCrashableTargetNode();
            if (target is null)
            {
                return "⚡ Crash Node";
            }

            return target.Health == NodeHealthStatus.Failed
                ? $"↻ Recover {target.Name}"
                : $"⚡ Crash {target.Name}";
        }
    }

    public string DelayActionLabel => CurrentState.Metrics.ArmedDelayCount > 0
        ? $"⏱ Delay ({CurrentState.Metrics.ArmedDelayCount})"
        : "⏱ Delay Msg";

    public string DropActionLabel => CurrentState.Metrics.ArmedDropCount > 0
        ? $"✕ Drop ({CurrentState.Metrics.ArmedDropCount})"
        : "✕ Drop Msg";

    // Context-aware floating dock actions for all 4 scenarios
    public string Action1Label => CurrentState.ScenarioId switch
    {
        "logical-clocks" => $"+ Local Event ({GetSelectedOrFirstNode()?.Name ?? "P1"})",
        "global-snapshots" => $"📸 Snapshot ({GetSelectedOrFirstNode()?.Name ?? "Account B"})",
        "raft-leader-election" => TargetWorkerActionLabel,
        _ => "+ Submit Job"
    };

    public string Action2Label => CurrentState.ScenarioId switch
    {
        "logical-clocks" => $"✉ Send ({ShortName(GetSelectedOrFirstNode())} → {ShortName(GetNextPeerNode())})",
        "global-snapshots" => $"💸 Send $50 ({ShortName(GetSelectedOrFirstNode())} → {ShortName(GetNextPeerNode())})",
        "raft-leader-election" => $"⏱ Timeout ({ResolveTimeoutTargetRaftNode()?.Name ?? "Node 1"})",
        _ => TargetWorkerActionLabel
    };

    public string Action3Label => DelayActionLabel;

    public string Action4Label => DropActionLabel;

    public bool ShowAction4 => CurrentState.ScenarioId is "task-scheduling" or "raft-leader-election";

    public string BreadcrumbText => CurrentLevel switch
    {
        NavigationLevel.Cluster => "Cluster",
        NavigationLevel.Node => $"Cluster / {FindNode(ActiveNodeId)?.Name ?? "Machine"}",
        NavigationLevel.Module => $"Cluster / {FindNode(ActiveNodeId)?.Name ?? "Machine"} / {FindModule(ActiveNodeId, SelectedModuleId)?.Name ?? "Module"}",
        _ => "Cluster"
    };

    public bool CanGoBack => CurrentLevel != NavigationLevel.Cluster;

    public bool IsInNodeOrModuleView => CurrentLevel != NavigationLevel.Cluster;

    public bool CanOpenSelectedNodeInternals =>
        CurrentLevel == NavigationLevel.Cluster && !string.IsNullOrEmpty(SelectedNodeId);

    public bool HasChildModules => InspectorChildModules.Count > 0;

    public bool HasSelectedEvent => SelectedEvent is not null;

    public string SelectedEventRoute => SelectedEvent is null
        ? string.Empty
        : SelectedEvent.TargetNodeId is null
            ? FormatNodeName(SelectedEvent.SourceNodeId)
            : $"{FormatNodeName(SelectedEvent.SourceNodeId)} → {FormatNodeName(SelectedEvent.TargetNodeId)}";

    // Tab 0 = Packet Flow (Story), Tab 1 = Details & Why (keeps compatibility with existing tests)
    public bool IsStatusTabSelected => SelectedInspectorTabIndex == 0;

    public bool IsExplainTabSelected => SelectedInspectorTabIndex == 1;

    public string LeftToggleLabel => IsLeftSidebarOpen ? "◧" : "◧";

    public string RightToggleLabel => IsRightInspectorOpen ? "◨" : "◨";

    public string BottomToggleLabel => IsBottomTimelineOpen ? "Hide Timeline" : "Show Timeline";

    public NodeState? ActiveNode => FindNode(ActiveNodeId);

    private void InitializeSelectionForCurrentScenario()
    {
        _expandedNodeIds.Clear();
        CurrentLevel = NavigationLevel.Cluster;
        ActiveNodeId = null;
        SelectedModuleId = null;
        SelectedEventId = null;
        SelectedEvent = null;
        SelectedEventPayload.Clear();
        EventCategoryFilter = "All";
        EventNodeFilter = "All";
        EventSearchText = string.Empty;

        EventNodeOptions.Clear();
        EventNodeOptions.Add("All");
        foreach (var n in CurrentState.Nodes)
        {
            EventNodeOptions.Add(n.Id);
        }

        SelectedNodeId = CurrentState.ScenarioId switch
        {
            "task-scheduling" => CurrentState.Nodes.FirstOrDefault(n => n.Id == "coordinator")?.Id ?? CurrentState.Nodes.FirstOrDefault()?.Id,
            "global-snapshots" => CurrentState.Nodes.FirstOrDefault(n => n.Id == "acct-b")?.Id ?? CurrentState.Nodes.FirstOrDefault()?.Id,
            _ => CurrentState.Nodes.FirstOrDefault()?.Id
        };

        if (SelectedNodeId is not null)
        {
            _expandedNodeIds.Add(SelectedNodeId);
        }

        StatusBannerText = IsPrototypeStub
            ? "Phase 0 Prototype Shell — Sample snapshot data is shown."
            : CurrentState.ScenarioId switch
            {
                "logical-clocks" => "Press Play (Space) or Step (N) to replay Lamport & Vector Clocks.",
                "global-snapshots" => "Press Play (Space) or Step (N) to watch Markers conserve $1,000.",
                "raft-leader-election" => "Press Play (Space) to elect a Leader — then crash it to test failover.",
                _ => "Press Play (Space) or Step (N) to trace Job J1 through the cluster."
            };
    }

    private void OnEngineStateChanged(object? sender, SimulationState newState)
    {
        CurrentState = newState;
        RefreshAllDerivedCollections();
    }

    partial void OnActiveScenarioChanged(ScenarioDescriptor value)
    {
        if (_suppressScenarioChange || value is null || value.Id == CurrentState.ScenarioId)
        {
            return;
        }

        SelectScenario(value.Id);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(PlayPauseLabel));
    }

    partial void OnVisualTimeMsChanged(double value)
    {
        OnPropertyChanged(nameof(SimulatedTimeDisplay));
    }

    partial void OnSelectedInspectorTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsStatusTabSelected));
        OnPropertyChanged(nameof(IsExplainTabSelected));
    }

    partial void OnHideHeartbeatsChanged(bool value)
    {
        OnPropertyChanged(nameof(HeartbeatToggleLabel));
        RefreshFilteredEvents();
    }

    partial void OnIsLeftSidebarOpenChanged(bool value) => OnPropertyChanged(nameof(LeftToggleLabel));

    partial void OnIsRightInspectorOpenChanged(bool value) => OnPropertyChanged(nameof(RightToggleLabel));

    partial void OnIsBottomTimelineOpenChanged(bool value) => OnPropertyChanged(nameof(BottomToggleLabel));

    partial void OnEventCategoryFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllCategoryActive));
        OnPropertyChanged(nameof(IsFaultsCategoryActive));
        OnPropertyChanged(nameof(IsProtocolCategoryActive));
        RefreshFilteredEvents();
    }

    partial void OnEventNodeFilterChanged(string value) => RefreshFilteredEvents();

    partial void OnEventSearchTextChanged(string value) => RefreshFilteredEvents();

    public void AdvancePlaybackFrame(double deltaRealSeconds)
    {
        if (!IsPlaying || !CanRunLivePlayback || deltaRealSeconds <= 0)
        {
            return;
        }

        var nextEventTime = _engine.PeekNextEventTimeMs();
        if (nextEventTime is null)
        {
            IsPlaying = false;
            StatusBannerText = "Simulation idle — use the bottom dock to send work or inject faults.";
            return;
        }

        var deltaSimMs = Math.Min(deltaRealSeconds, 0.1) * BaseSimMsPerRealSecond * SpeedMultiplier;
        VisualTimeMs += deltaSimMs;

        var executedThisFrame = 0;
        while (executedThisFrame < 12 &&
               _engine.PeekNextEventTimeMs() is long scheduledMs &&
               VisualTimeMs >= scheduledMs)
        {
            var result = _engine.Step();
            if (!result.Executed)
            {
                IsPlaying = false;
                break;
            }

            if (result.Event is not null && (!HideHeartbeats || result.Event.Category != EventCategory.Heartbeats))
            {
                StatusBannerText = $"{result.Event.Summary}";
            }

            executedThisFrame++;
        }
    }

    [RelayCommand]
    public void SelectScenario(string? scenarioId)
    {
        if (string.IsNullOrWhiteSpace(scenarioId)) return;

        var descriptor = Scenarios.FirstOrDefault(s => s.Id == scenarioId);
        if (descriptor is null || !descriptor.IsAvailable) return;

        IsPlaying = false;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine = ScenarioFactory.Create(scenarioId, seed: 42);
        _engine.StateChanged += OnEngineStateChanged;

        _suppressScenarioChange = true;
        ActiveScenario = descriptor;
        _suppressScenarioChange = false;

        CurrentState = _engine.GetState();
        VisualTimeMs = CurrentState.SimulatedTimeMs;

        InitializeSelectionForCurrentScenario();
        OnPropertyChanged(nameof(IsPrototypeStub));
        OnPropertyChanged(nameof(CanRunLivePlayback));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(HasSelectedEvent));
        OnPropertyChanged(nameof(SelectedEventRoute));
        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
        LayoutResetRequested?.Invoke();
    }

    [RelayCommand]
    public void ToggleHideHeartbeats() => HideHeartbeats = !HideHeartbeats;

    [RelayCommand]
    public void PlaySimulation()
    {
        if (!CanRunLivePlayback) return;

        if (_engine.PeekNextEventTimeMs() is null && CurrentState.ScenarioId == "task-scheduling")
        {
            _engine.DispatchAction(new SubmitJobAction("New Job", TaskCount: 1));
        }

        IsPlaying = true;
    }

    [RelayCommand]
    public void PauseSimulation() => IsPlaying = false;

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying) PauseSimulation();
        else PlaySimulation();
    }

    [RelayCommand]
    public void StepSimulation()
    {
        IsPlaying = false;
        var result = _engine.Step();
        CurrentState = result.State;
        VisualTimeMs = CurrentState.SimulatedTimeMs;

        if (result.Executed && result.Event is not null)
        {
            StatusBannerText = $"{result.Event.Summary}";
        }
        else if (!string.IsNullOrWhiteSpace(result.Note))
        {
            StatusBannerText = result.Note;
        }

        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void ResetSimulation()
    {
        IsPlaying = false;
        CurrentState = _engine.Reset();
        VisualTimeMs = CurrentState.SimulatedTimeMs;
        SelectedEventId = null;
        SelectedEvent = null;
        SelectedEventPayload.Clear();
        StatusBannerText = IsPrototypeStub
            ? "Prototype snapshot reset."
            : "Reset to t = 0 ms (same seed and starting state).";

        LayoutResetRequested?.Invoke();
        OnPropertyChanged(nameof(HasSelectedEvent));
        OnPropertyChanged(nameof(SelectedEventRoute));
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void ExecuteAction1()
    {
        if (!CanRunLivePlayback) return;

        switch (CurrentState.ScenarioId)
        {
            case "logical-clocks":
                var proc = GetSelectedOrFirstNode();
                if (proc is not null)
                {
                    _engine.DispatchAction(new TriggerLocalEventAction(proc.Id));
                    if (!IsPlaying) StepSimulation();
                }
                break;

            case "global-snapshots":
                var init = GetSelectedOrFirstNode();
                if (init is not null)
                {
                    _engine.DispatchAction(new InitiateSnapshotAction(init.Id));
                    if (!IsPlaying) StepSimulation();
                }
                break;

            case "raft-leader-election":
                ToggleCrashTargetWorker();
                break;

            default:
                SubmitJob();
                break;
        }
    }

    [RelayCommand]
    public void ExecuteAction2()
    {
        if (!CanRunLivePlayback) return;

        switch (CurrentState.ScenarioId)
        {
            case "logical-clocks":
                var srcP = GetSelectedOrFirstNode();
                var dstP = GetNextPeerNode();
                if (srcP is not null && dstP is not null)
                {
                    _engine.DispatchAction(new SendPeerMessageAction(srcP.Id, dstP.Id));
                    if (!IsPlaying) StepSimulation();
                }
                break;

            case "global-snapshots":
                var srcA = GetSelectedOrFirstNode();
                var dstA = GetNextPeerNode();
                if (srcA is not null && dstA is not null)
                {
                    _engine.DispatchAction(new SendTransferAction(srcA.Id, dstA.Id, 50));
                    if (!IsPlaying) StepSimulation();
                }
                break;

            case "raft-leader-election":
                var target = ResolveTimeoutTargetRaftNode();
                if (target is not null)
                {
                    _engine.DispatchAction(new TriggerElectionTimeoutAction(target.Id));
                    if (!IsPlaying) StepSimulation();
                }
                break;

            default:
                ToggleCrashTargetWorker();
                break;
        }
    }

    [RelayCommand]
    public void ExecuteAction3() => DelayNextMessage();

    [RelayCommand]
    public void ExecuteAction4() => DropNextMessage();

    [RelayCommand]
    public void SubmitJob()
    {
        if (!CanRunLivePlayback) return;

        _engine.DispatchAction(new SubmitJobAction("User Job", TaskCount: 1));
        if (!IsPlaying) StepSimulation();
        else StatusBannerText = "Submitted a new job from Client.";
    }

    [RelayCommand]
    public void ToggleCrashTargetWorker()
    {
        if (!CanRunLivePlayback) return;

        var target = ResolveCrashableTargetNode();
        if (target is null) return;

        if (target.Health == NodeHealthStatus.Failed)
        {
            _engine.DispatchAction(new RecoverWorkerAction(target.Id));
        }
        else
        {
            _engine.DispatchAction(new CrashWorkerAction(target.Id));
        }

        if (!IsPlaying) StepSimulation();
    }

    [RelayCommand]
    public void DelayNextMessage()
    {
        if (!CanRunLivePlayback) return;

        var extraMs = CurrentState.ScenarioId == "task-scheduling" ? 2600 : 650;
        _engine.DispatchAction(new DelayNextMessageAction(ExtraDelayMs: extraMs));
        StatusBannerText = $"Armed +{extraMs} ms delay on the next message.";
    }

    [RelayCommand]
    public void DropNextMessage()
    {
        if (!CanRunLivePlayback) return;

        _engine.DispatchAction(new DropNextMessageAction());
        StatusBannerText = "Armed packet drop on the next message.";
    }

    [RelayCommand]
    public void ToggleLeftSidebar() => IsLeftSidebarOpen = !IsLeftSidebarOpen;

    [RelayCommand]
    public void ToggleRightInspector() => IsRightInspectorOpen = !IsRightInspectorOpen;

    [RelayCommand]
    public void ToggleBottomTimeline() => IsBottomTimelineOpen = !IsBottomTimelineOpen;

    [RelayCommand]
    public void ShowStatusTab() => SelectedInspectorTabIndex = 0;

    [RelayCommand]
    public void ShowExplainTab() => SelectedInspectorTabIndex = 1;

    [RelayCommand]
    public void SelectClusterOverview()
    {
        CurrentLevel = NavigationLevel.Cluster;
        ActiveNodeId = null;
        SelectedNodeId = null;
        SelectedModuleId = null;
        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void NavigateToCluster()
    {
        CurrentLevel = NavigationLevel.Cluster;
        ActiveNodeId = null;
        SelectedModuleId = null;
        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void SelectNode(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            SelectedNodeId = null;
            SelectedModuleId = null;
            if (CurrentLevel == NavigationLevel.Module)
            {
                CurrentLevel = NavigationLevel.Node;
            }
        }
        else if (FindNode(nodeId) is not null)
        {
            SelectedNodeId = nodeId;
            if (CurrentLevel == NavigationLevel.Cluster)
            {
                SelectedModuleId = null;
            }
            else if (ActiveNodeId != nodeId)
            {
                ActiveNodeId = nodeId;
                SelectedModuleId = null;
                CurrentLevel = NavigationLevel.Node;
            }
        }

        IsRightInspectorOpen = true;
        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void ToggleNodeExpanded(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;

        if (!_expandedNodeIds.Add(nodeId))
        {
            _expandedNodeIds.Remove(nodeId);
        }

        RefreshClusterOutline();
    }

    [RelayCommand]
    public void OpenNode(string? nodeId)
    {
        var targetId = nodeId ?? SelectedNodeId;
        if (string.IsNullOrWhiteSpace(targetId) || FindNode(targetId) is null)
        {
            return;
        }

        _expandedNodeIds.Add(targetId);
        ActiveNodeId = targetId;
        SelectedNodeId = targetId;
        SelectedModuleId = null;
        CurrentLevel = NavigationLevel.Node;
        IsRightInspectorOpen = true;
        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void SelectModule(string? moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            if (CurrentLevel == NavigationLevel.Module)
            {
                SelectedModuleId = null;
                CurrentLevel = NavigationLevel.Node;
                NotifyNavigationChanged();
                RefreshAllDerivedCollections();
            }
            return;
        }

        var owner = CurrentState.Nodes.FirstOrDefault(n => n.Modules.Any(m => m.Id == moduleId));
        if (owner is null) return;

        _expandedNodeIds.Add(owner.Id);
        ActiveNodeId = owner.Id;
        SelectedNodeId = owner.Id;
        SelectedModuleId = moduleId;
        CurrentLevel = NavigationLevel.Module;
        IsRightInspectorOpen = true;
        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void GoBack()
    {
        if (CurrentLevel == NavigationLevel.Module)
        {
            SelectedModuleId = null;
            CurrentLevel = NavigationLevel.Node;
        }
        else if (CurrentLevel == NavigationLevel.Node)
        {
            ActiveNodeId = null;
            SelectedModuleId = null;
            CurrentLevel = NavigationLevel.Cluster;
        }

        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void SetEventCategoryFilter(string category) => EventCategoryFilter = category;

    [RelayCommand]
    public void SelectEvent(EventListItem? item)
    {
        if (item is null || item.Record.Id == SelectedEventId)
        {
            SelectedEventId = null;
            SelectedEvent = null;
            SelectedEventPayload.Clear();
            OnPropertyChanged(nameof(HasSelectedEvent));
            OnPropertyChanged(nameof(SelectedEventRoute));
            RefreshFilteredEvents();
            return;
        }

        SelectedEventId = item.Record.Id;
        SelectedEvent = item.Record;
        SelectedEventPayload.Clear();
        foreach (var pair in item.Record.Payload)
        {
            SelectedEventPayload.Add(new KeyValueItem(pair.Key, pair.Value));
        }

        if (CurrentLevel == NavigationLevel.Cluster)
        {
            SelectedNodeId = item.Record.SourceNodeId;
        }
        else if (ActiveNodeId == item.Record.SourceNodeId && item.Record.ModuleId is not null)
        {
            SelectedModuleId = item.Record.ModuleId;
            CurrentLevel = NavigationLevel.Module;
        }

        IsRightInspectorOpen = true;
        OnPropertyChanged(nameof(HasSelectedEvent));
        OnPropertyChanged(nameof(SelectedEventRoute));
        NotifyNavigationChanged();
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void OpenTheoryNotes()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ActiveScenario.TheoryNoteUrl) { UseShellExecute = true });
        }
        catch
        {
            StatusBannerText = $"Theory notes URL: {ActiveScenario.TheoryNoteUrl}";
        }
    }

    private NodeState? GetSelectedOrFirstNode() =>
        FindNode(SelectedNodeId) ?? CurrentState.Nodes.FirstOrDefault();

    private NodeState? GetNextPeerNode()
    {
        if (CurrentState.Nodes.Count < 2) return null;
        var current = GetSelectedOrFirstNode();
        if (current is null) return CurrentState.Nodes[0];

        var idx = -1;
        for (var i = 0; i < CurrentState.Nodes.Count; i++)
        {
            if (CurrentState.Nodes[i].Id == current.Id)
            {
                idx = i;
                break;
            }
        }

        return CurrentState.Nodes[(idx + 1) % CurrentState.Nodes.Count];
    }

    private static string ShortName(NodeState? node)
    {
        if (node is null) return "?";
        return node.Name
            .Replace("Process ", string.Empty)
            .Replace("Account ", string.Empty);
    }

    private NodeState? ResolveCrashableTargetNode()
    {
        if (CurrentState.ScenarioId == "raft-leader-election")
        {
            if (FindNode(SelectedNodeId) is { Role: NodeRole.RaftServer } selectedRaft)
            {
                return selectedRaft;
            }

            return CurrentState.Nodes.FirstOrDefault(n => n.StatusSummary.StartsWith("LEADER", StringComparison.Ordinal))
                   ?? CurrentState.Nodes.FirstOrDefault(n => n.Health != NodeHealthStatus.Failed)
                   ?? CurrentState.Nodes.FirstOrDefault();
        }

        if (FindNode(SelectedNodeId) is { Role: NodeRole.Worker } selectedWorker)
        {
            return selectedWorker;
        }

        return CurrentState.Nodes.FirstOrDefault(n => n.Role == NodeRole.Worker && n.ActiveTaskCount > 0)
               ?? CurrentState.Nodes.FirstOrDefault(n => n.Role == NodeRole.Worker && n.Health != NodeHealthStatus.Failed)
               ?? CurrentState.Nodes.FirstOrDefault(n => n.Role == NodeRole.Worker);
    }

    private NodeState? ResolveTimeoutTargetRaftNode()
    {
        if (FindNode(SelectedNodeId) is { Role: NodeRole.RaftServer, Health: not NodeHealthStatus.Failed } sel &&
            !sel.StatusSummary.StartsWith("LEADER", StringComparison.Ordinal))
        {
            return sel;
        }

        return CurrentState.Nodes.FirstOrDefault(n =>
            n.Role == NodeRole.RaftServer &&
            n.Health != NodeHealthStatus.Failed &&
            !n.StatusSummary.StartsWith("LEADER", StringComparison.Ordinal))
            ?? CurrentState.Nodes.FirstOrDefault(n => n.Health != NodeHealthStatus.Failed);
    }

    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(BreadcrumbText));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsInNodeOrModuleView));
        OnPropertyChanged(nameof(CanOpenSelectedNodeInternals));
        OnPropertyChanged(nameof(ActiveNode));
        OnPropertyChanged(nameof(SimulatedTimeDisplay));
        NotifyActionLabelsChanged();
    }

    private void NotifyActionLabelsChanged()
    {
        OnPropertyChanged(nameof(TargetWorkerActionLabel));
        OnPropertyChanged(nameof(DelayActionLabel));
        OnPropertyChanged(nameof(DropActionLabel));
        OnPropertyChanged(nameof(Action1Label));
        OnPropertyChanged(nameof(Action2Label));
        OnPropertyChanged(nameof(Action3Label));
        OnPropertyChanged(nameof(Action4Label));
        OnPropertyChanged(nameof(ShowAction4));
    }

    private void RefreshAllDerivedCollections()
    {
        RefreshScenarioItems();
        RefreshClusterOutline();
        RefreshInspector();
        RefreshFilteredEvents();
        OnPropertyChanged(nameof(SimulatedTimeDisplay));
        OnPropertyChanged(nameof(HasChildModules));
        OnPropertyChanged(nameof(CanOpenSelectedNodeInternals));
        OnPropertyChanged(nameof(ActiveNode));
        NotifyActionLabelsChanged();
    }

    private void RefreshScenarioItems()
    {
        ScenarioItems.Clear();
        foreach (var s in Scenarios)
        {
            ScenarioItems.Add(new ScenarioListItem(s, s.Id == CurrentState.ScenarioId));
        }
    }

    private void RefreshClusterOutline()
    {
        ClusterNodes.Clear();
        foreach (var node in CurrentState.Nodes)
        {
            var isExpanded = _expandedNodeIds.Contains(node.Id);
            var modules = node.Modules
                .Select(m => new ModuleListItem(
                    m.Id,
                    m.NodeId,
                    m.Name,
                    m.ShortRole,
                    FormatModuleStatus(m.Status),
                    m.Id == SelectedModuleId))
                .ToList();

            var glyph = node.Role switch
            {
                NodeRole.Client => "◰",
                NodeRole.Coordinator => "◈",
                NodeRole.Process => "◷",
                NodeRole.Account => "$",
                NodeRole.RaftServer => node.StatusSummary.StartsWith("LEADER", StringComparison.Ordinal) ? "★" : "◈",
                _ => "▤"
            };

            ClusterNodes.Add(new NodeOutlineItem(
                node.Id,
                node.Name,
                node.Role,
                glyph,
                node.Role.ToString().ToUpperInvariant(),
                FormatNodeHealth(node.Health),
                node.Id == SelectedNodeId,
                node.Id == ActiveNodeId,
                isExpanded,
                isExpanded ? "▾" : "▸",
                modules));
        }
    }

    private void RefreshInspector()
    {
        InspectorQueueItems.Clear();
        InspectorCounters.Clear();
        InspectorQuickStats.Clear();
        InspectorChildModules.Clear();

        if (CurrentLevel == NavigationLevel.Module && FindModule(ActiveNodeId, SelectedModuleId) is { } module)
        {
            var parentNode = FindNode(module.NodeId);
            InspectorKindLabel = "MODULE";
            InspectorTitle = module.Name;
            InspectorSubtitle = $"{parentNode?.Name ?? module.NodeId} · {module.ShortRole}";
            InspectorStatusLabel = FormatModuleStatus(module.Status);
            InspectorPlainDescription = module.PlainDescription;
            InspectorQueueTitle = "Local State & Queue";

            if (module.QueueItems.Count == 0)
            {
                InspectorQueueItems.Add("Empty right now");
            }
            else
            {
                foreach (var q in module.QueueItems)
                {
                    InspectorQueueItems.Add(q);
                }
            }

            foreach (var c in module.Counters)
            {
                InspectorCounters.Add(new KeyValueItem(c.Key, c.Value));
            }
        }
        else if (FindNode(ActiveNodeId ?? SelectedNodeId) is { } node)
        {
            InspectorKindLabel = node.Role.ToString().ToUpperInvariant();
            InspectorTitle = node.Name;
            InspectorSubtitle = node.StatusSummary;
            InspectorStatusLabel = FormatNodeHealth(node.Health);
            InspectorPlainDescription = node.PlainDescription;
            InspectorQueueTitle = "Live Activity";

            foreach (var item in node.InboxItems)
            {
                InspectorQueueItems.Add(item);
            }

            foreach (var item in node.OutboxItems)
            {
                if (!InspectorQueueItems.Contains(item))
                {
                    InspectorQueueItems.Add(item);
                }
            }

            if (InspectorQueueItems.Count == 0)
            {
                InspectorQueueItems.Add("Idle — no buffered items");
            }

            if (node.Role == NodeRole.RaftServer)
            {
                InspectorCounters.Add(new KeyValueItem("Current Term", node.ActiveTaskCount.ToString()));
                InspectorCounters.Add(new KeyValueItem("Quorum", $"{node.Capacity} / {CurrentState.Nodes.Count}"));
            }
            else if (node.Role == NodeRole.Account)
            {
                InspectorCounters.Add(new KeyValueItem("Balance (Sim)", $"${node.ActiveTaskCount}"));
            }
            else if (node.Role == NodeRole.Process)
            {
                InspectorCounters.Add(new KeyValueItem("Lamport L", node.ActiveTaskCount.ToString()));
            }
            else
            {
                InspectorCounters.Add(new KeyValueItem("Active Tasks", $"{node.ActiveTaskCount} / {node.Capacity}"));
            }

            InspectorCounters.Add(new KeyValueItem("Modules", node.Modules.Count.ToString()));
            InspectorCounters.Add(new KeyValueItem("Sim Clock", $"{CurrentState.SimulatedTimeMs} ms"));

            foreach (var m in node.Modules)
            {
                InspectorChildModules.Add(new ModuleListItem(
                    m.Id,
                    m.NodeId,
                    m.Name,
                    m.ShortRole,
                    FormatModuleStatus(m.Status),
                    m.Id == SelectedModuleId));
            }
        }
        else
        {
            var metrics = CurrentState.Metrics;
            InspectorKindLabel = "CLUSTER";
            InspectorTitle = CurrentState.ScenarioTitle;
            InspectorSubtitle = $"{CurrentState.Nodes.Count} machines · Seed #{CurrentState.Seed}";
            InspectorStatusLabel = "● Active";
            InspectorPlainDescription = ActiveScenario.ShortSummary;
            InspectorQueueTitle = "Cluster Nodes";

            foreach (var n in CurrentState.Nodes)
            {
                InspectorQueueItems.Add($"{n.Name}: {n.StatusSummary}");
            }

            if (CurrentState.ScenarioId == "task-scheduling")
            {
                InspectorCounters.Add(new KeyValueItem("Tasks (Q/R/D)", $"{metrics.PendingTasks}/{metrics.RunningTasks}/{metrics.CompletedTasks}"));
                InspectorCounters.Add(new KeyValueItem("Retries", metrics.RetryCount.ToString()));
                InspectorCounters.Add(new KeyValueItem("Stale Ignored", metrics.StaleResultsIgnored.ToString()));
            }
            else if (CurrentState.ScenarioId == "global-snapshots")
            {
                InspectorCounters.Add(new KeyValueItem("Snapshots", metrics.CompletedTasks.ToString()));
                InspectorCounters.Add(new KeyValueItem("Last Total", metrics.LastJobDurationMs.HasValue ? $"${metrics.LastJobDurationMs.Value}" : "Pending"));
                InspectorCounters.Add(new KeyValueItem("Messages", metrics.MessagesSent.ToString()));
            }
            else if (CurrentState.ScenarioId == "raft-leader-election")
            {
                InspectorCounters.Add(new KeyValueItem("Alive", $"{metrics.QueueDepth}/{CurrentState.Nodes.Count}"));
                InspectorCounters.Add(new KeyValueItem("Max Term", metrics.DispatchedTasks.ToString()));
                InspectorCounters.Add(new KeyValueItem("Leaders", metrics.CompletedTasks.ToString()));
            }
            else
            {
                InspectorCounters.Add(new KeyValueItem("Events", metrics.CompletedTasks.ToString()));
                InspectorCounters.Add(new KeyValueItem("Messages", metrics.MessagesSent.ToString()));
            }
        }

        // Top 3 compact stats for the sleek always-visible strip
        foreach (var item in InspectorCounters.Take(3))
        {
            InspectorQuickStats.Add(item);
        }

        OnPropertyChanged(nameof(HasChildModules));
        OnPropertyChanged(nameof(CanOpenSelectedNodeInternals));
    }

    private void RefreshFilteredEvents()
    {
        FilteredEvents.Clear();

        foreach (var evt in CurrentState.EventLog.OrderByDescending(e => e.Sequence))
        {
            // Hide noisy background heartbeats by default unless user explicitly enables them or picks Heartbeats category
            if (HideHeartbeats &&
                evt.Category == EventCategory.Heartbeats &&
                !string.Equals(EventCategoryFilter, "Heartbeats", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (EventCategoryFilter != "All" &&
                !string.Equals(evt.Category.ToString(), EventCategoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (EventNodeFilter != "All" &&
                !string.Equals(evt.SourceNodeId, EventNodeFilter, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(evt.TargetNodeId, EventNodeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(EventSearchText))
            {
                var q = EventSearchText.Trim();
                var matches = evt.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              evt.Type.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              evt.SourceNodeId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              (evt.TargetNodeId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
                if (!matches)
                {
                    continue;
                }
            }

            var route = evt.TargetNodeId is null
                ? FormatNodeName(evt.SourceNodeId)
                : $"{FormatNodeName(evt.SourceNodeId)} → {FormatNodeName(evt.TargetNodeId)}";

            var (friendlyTag, dotColor) = FormatEventPresentation(evt.Type);

            FilteredEvents.Add(new EventListItem(
                evt,
                $"{evt.TimestampMs} ms",
                route,
                evt.Category.ToString(),
                friendlyTag,
                dotColor,
                evt.Id == SelectedEventId));
        }

        OnPropertyChanged(nameof(HasFilteredEvents));
        OnPropertyChanged(nameof(EmptyFlowHint));
    }

    public static (string Tag, string DotColorHex) FormatEventPresentation(SimulationEventType type) => type switch
    {
        SimulationEventType.JobSubmitted => ("📥 Submit Job", "#2E9E63"),
        SimulationEventType.JobReceived => ("📨 Job Received", "#2B7DE9"),
        SimulationEventType.TaskEnqueued => ("◷ Queued", "#D9822B"),
        SimulationEventType.TaskDispatched => ("📤 Dispatch", "#2B7DE9"),
        SimulationEventType.TaskStarted => ("▶ Running", "#2E9E63"),
        SimulationEventType.TaskExecutionFinished => ("✔ Executed", "#2E9E63"),
        SimulationEventType.ResultReceived => ("✅ Accepted", "#2E9E63"),
        SimulationEventType.JobFinished => ("🏁 Job Done", "#2E9E63"),
        SimulationEventType.ClientResponded => ("🎉 Replied", "#2E9E63"),
        SimulationEventType.WorkerCrashed => ("⚡ Crashed", "#D9534F"),
        SimulationEventType.WorkerTimedOut => ("⚠ Timed Out", "#D9534F"),
        SimulationEventType.TaskRequeued => ("↻ Requeued", "#D9822B"),
        SimulationEventType.WorkerRecovered => ("💚 Recovered", "#2E9E63"),
        SimulationEventType.MessageDropped => ("✕ Dropped", "#D9534F"),
        SimulationEventType.StaleResultIgnored => ("🛡 Stale Ignored", "#7C4DFF"),
        SimulationEventType.HeartbeatSent or SimulationEventType.HeartbeatReceived or
            SimulationEventType.AppendEntriesSent or SimulationEventType.AppendEntriesReceived => ("♡ Heartbeat", "#8B929E"),
        SimulationEventType.LocalEvent => ("◷ Local Step", "#2B7DE9"),
        SimulationEventType.LogicalMessageSent => ("📤 Send Msg", "#2E9E63"),
        SimulationEventType.LogicalMessageReceived => ("📥 Merge Clock", "#7C4DFF"),
        SimulationEventType.TransferSent => ("💸 Transfer Out", "#2E9E63"),
        SimulationEventType.TransferReceived => ("💰 Transfer In", "#2E9E63"),
        SimulationEventType.SnapshotInitiated => ("📸 Start Cut", "#7C4DFF"),
        SimulationEventType.MarkerReceived => ("⚑ Marker", "#7C4DFF"),
        SimulationEventType.ChannelClosed => ("🔒 Channel Closed", "#2B7DE9"),
        SimulationEventType.SnapshotCompleted => ("✅ Conserved", "#2E9E63"),
        SimulationEventType.CandidateStartedElection => ("🗳 Candidate", "#D9822B"),
        SimulationEventType.VoteRequested => ("🗳 Vote +1", "#7C4DFF"),
        SimulationEventType.VoteGranted => ("✓ Voted", "#2E9E63"),
        SimulationEventType.VoteDenied => ("✕ Denied", "#D9534F"),
        SimulationEventType.LeaderElected => ("👑 Leader", "#D9822B"),
        SimulationEventType.LeaderSteppedDown => ("↓ Step Down", "#8B929E"),
        _ => (type.ToString(), "#646D7C")
    };

    private NodeState? FindNode(string? nodeId) =>
        string.IsNullOrWhiteSpace(nodeId)
            ? null
            : CurrentState.Nodes.FirstOrDefault(n => n.Id == nodeId);

    private ModuleState? FindModule(string? nodeId, string? moduleId) =>
        string.IsNullOrWhiteSpace(moduleId)
            ? null
            : FindNode(nodeId)?.Modules.FirstOrDefault(m => m.Id == moduleId);

    private string FormatNodeName(string nodeId) =>
        FindNode(nodeId)?.Name ?? nodeId;

    public static string FormatNodeHealth(NodeHealthStatus health) => health switch
    {
        NodeHealthStatus.Healthy => "● Healthy",
        NodeHealthStatus.Processing => "▶ Processing",
        NodeHealthStatus.Suspected => "⚠ Suspected",
        NodeHealthStatus.Failed => "✕ Failed",
        NodeHealthStatus.Recovering => "↻ Recovering",
        _ => "○ Idle"
    };

    public static string FormatModuleStatus(ModuleStatus status) => status switch
    {
        ModuleStatus.Healthy => "● Healthy",
        ModuleStatus.Processing => "▶ Processing",
        ModuleStatus.Queued => "◷ Queued",
        ModuleStatus.Suspected => "⚠ Suspected",
        ModuleStatus.Failed => "✕ Failed",
        ModuleStatus.Recovering => "↻ Recovering",
        _ => "○ Idle"
    };
}
