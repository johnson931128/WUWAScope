using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WUWAScope.Simulation.Contracts;
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
    bool IsSelected);

public partial class MainWindowViewModel : ViewModelBase
{
    private const double BaseSimMsPerRealSecond = 420.0;

    private readonly ISimulationEngine _engine;
    private readonly HashSet<string> _expandedNodeIds = ["coordinator"];

    public event Action? LayoutResetRequested;

    [ObservableProperty]
    private SimulationState _currentState;

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
    private string _inspectorSubtitle = "1 Client · 1 Coordinator · 3 Workers";

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
        ActiveScenario = Scenarios[0];
        SpeedOptions = ["0.5x", "1.0x", "2.0x", "4.0x"];
        EventCategoryOptions = ["All", "Jobs", "Network", "Heartbeats", "Faults"];
        EventNodeOptions = ["All", "client", "coordinator", "worker-1", "worker-2", "worker-3"];

        StatusBannerText = IsPrototypeStub
            ? "Phase 0 Prototype Shell — Sample snapshot data is shown."
            : "Ready! Press Play (Space) or Step (N) to watch Job J1 move through the cluster.";

        SelectedNodeId = "coordinator";
        RefreshAllDerivedCollections();
    }

    public IReadOnlyList<ScenarioDescriptor> Scenarios { get; }

    public ScenarioDescriptor ActiveScenario { get; }

    public IReadOnlyList<string> SpeedOptions { get; }

    public IReadOnlyList<string> EventCategoryOptions { get; }

    public IReadOnlyList<string> EventNodeOptions { get; }

    public ObservableCollection<NodeOutlineItem> ClusterNodes { get; } = [];

    public ObservableCollection<string> InspectorQueueItems { get; } = [];

    public ObservableCollection<KeyValueItem> InspectorCounters { get; } = [];

    public ObservableCollection<ModuleListItem> InspectorChildModules { get; } = [];

    public ObservableCollection<EventListItem> FilteredEvents { get; } = [];

    public ObservableCollection<KeyValueItem> SelectedEventPayload { get; } = [];

    public bool IsPrototypeStub => _engine.IsPrototypeStub;

    public bool CanRunLivePlayback => !_engine.IsPrototypeStub;

    public bool CanPlay => CanRunLivePlayback && !IsPlaying;

    public bool CanPause => CanRunLivePlayback && IsPlaying;

    public double SpeedMultiplier => SelectedSpeedLabel switch
    {
        "0.5x" => 0.5,
        "2.0x" => 2.0,
        "4.0x" => 4.0,
        _ => 1.0
    };

    public string SimulatedTimeDisplay => $"t = {Math.Round(VisualTimeMs):0} ms (Simulated)";

    public string TargetWorkerActionLabel
    {
        get
        {
            var worker = ResolveTargetWorker();
            if (worker is null)
            {
                return "⚡ Crash Worker";
            }

            return worker.Health == NodeHealthStatus.Failed
                ? $"↻ Recover {worker.Name}"
                : $"⚡ Crash {worker.Name}";
        }
    }

    public string DelayActionLabel => CurrentState.Metrics.ArmedDelayCount > 0
        ? $"⏱ Delay Msg ({CurrentState.Metrics.ArmedDelayCount} armed)"
        : "⏱ Delay Next Msg";

    public string DropActionLabel => CurrentState.Metrics.ArmedDropCount > 0
        ? $"✕ Drop Msg ({CurrentState.Metrics.ArmedDropCount} armed)"
        : "✕ Drop Next Msg";

    public string BreadcrumbText => CurrentLevel switch
    {
        NavigationLevel.Cluster => "Cluster Overview",
        NavigationLevel.Node => $"Cluster  /  {FindNode(ActiveNodeId)?.Name ?? "Machine"}",
        NavigationLevel.Module => $"Cluster  /  {FindNode(ActiveNodeId)?.Name ?? "Machine"}  /  {FindModule(ActiveNodeId, SelectedModuleId)?.Name ?? "Module"}",
        _ => "Cluster Overview"
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

    public bool IsStatusTabSelected => SelectedInspectorTabIndex == 0;

    public bool IsExplainTabSelected => SelectedInspectorTabIndex == 1;

    public string LeftToggleLabel => IsLeftSidebarOpen ? "Hide Menu" : "Show Menu";

    public string RightToggleLabel => IsRightInspectorOpen ? "Hide Details" : "Show Details";

    public string BottomToggleLabel => IsBottomTimelineOpen ? "Hide Timeline" : "Show Timeline";

    public NodeState? ActiveNode => FindNode(ActiveNodeId);

    private void OnEngineStateChanged(object? sender, SimulationState newState)
    {
        CurrentState = newState;
        RefreshAllDerivedCollections();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanPause));
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

    partial void OnIsLeftSidebarOpenChanged(bool value) => OnPropertyChanged(nameof(LeftToggleLabel));

    partial void OnIsRightInspectorOpenChanged(bool value) => OnPropertyChanged(nameof(RightToggleLabel));

    partial void OnIsBottomTimelineOpenChanged(bool value) => OnPropertyChanged(nameof(BottomToggleLabel));

    partial void OnEventCategoryFilterChanged(string value) => RefreshFilteredEvents();

    partial void OnEventNodeFilterChanged(string value) => RefreshFilteredEvents();

    partial void OnEventSearchTextChanged(string value) => RefreshFilteredEvents();

    /// <summary>
    /// Advances the smooth visual playback clock during Play mode and executes engine events
    /// strictly at their exact simulated timestamps. Speed never alters event ordering or outcomes.
    /// </summary>
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
            StatusBannerText = "Simulation idle — click '+ Submit Job' to send more work.";
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

            if (result.Event is not null)
            {
                StatusBannerText = $"[{result.Event.TimestampMs} ms] {result.Event.Summary}";
            }

            executedThisFrame++;
        }
    }

    [RelayCommand]
    public void PlaySimulation()
    {
        if (!CanRunLivePlayback)
        {
            return;
        }

        if (_engine.PeekNextEventTimeMs() is null)
        {
            _engine.DispatchAction(new SubmitJobAction("New Job", TaskCount: 1));
        }

        IsPlaying = true;
    }

    [RelayCommand]
    public void PauseSimulation()
    {
        IsPlaying = false;
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            PauseSimulation();
        }
        else
        {
            PlaySimulation();
        }
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
            StatusBannerText = $"Step #{result.Event.Sequence} ({result.Event.TimestampMs} ms): {result.Event.Summary}";
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
            : "Simulation reset to t = 0 ms (same seed and initial state).";

        LayoutResetRequested?.Invoke();
        OnPropertyChanged(nameof(HasSelectedEvent));
        OnPropertyChanged(nameof(SelectedEventRoute));
        RefreshAllDerivedCollections();
    }

    [RelayCommand]
    public void SubmitJob()
    {
        if (!CanRunLivePlayback)
        {
            return;
        }

        _engine.DispatchAction(new SubmitJobAction("User Job", TaskCount: 1));
        if (!IsPlaying)
        {
            StepSimulation();
        }
        else
        {
            StatusBannerText = "Queued a new job from Client.";
        }
    }

    [RelayCommand]
    public void ToggleCrashTargetWorker()
    {
        if (!CanRunLivePlayback)
        {
            return;
        }

        var target = ResolveTargetWorker();
        if (target is null)
        {
            return;
        }

        if (target.Health == NodeHealthStatus.Failed)
        {
            _engine.DispatchAction(new RecoverWorkerAction(target.Id));
        }
        else
        {
            _engine.DispatchAction(new CrashWorkerAction(target.Id));
        }

        if (!IsPlaying)
        {
            StepSimulation();
        }
    }

    [RelayCommand]
    public void DelayNextMessage()
    {
        if (!CanRunLivePlayback)
        {
            return;
        }

        _engine.DispatchAction(new DelayNextMessageAction(ExtraDelayMs: 2600));
        StatusBannerText = "Armed +2600 ms delay on the next job/task/result message (great for triggering retries & stale replies).";
    }

    [RelayCommand]
    public void DropNextMessage()
    {
        if (!CanRunLivePlayback)
        {
            return;
        }

        _engine.DispatchAction(new DropNextMessageAction());
        StatusBannerText = "Armed packet drop on the next job/task/result message.";
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
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }

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
        if (owner is null)
        {
            return;
        }

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
    public void SetEventCategoryFilter(string category)
    {
        EventCategoryFilter = category;
    }

    [RelayCommand]
    public void SelectEvent(EventListItem? item)
    {
        if (item is null)
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
            Process.Start(new ProcessStartInfo(ActiveScenario.TheoryNoteUrl)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            StatusBannerText = $"Theory notes URL: {ActiveScenario.TheoryNoteUrl}";
        }
    }

    private NodeState? ResolveTargetWorker()
    {
        if (FindNode(SelectedNodeId) is { Role: NodeRole.Worker } selectedWorker)
        {
            return selectedWorker;
        }

        // Prefer a currently busy worker so "Crash Worker" immediately demonstrates active failover
        return CurrentState.Nodes.FirstOrDefault(n => n.Role == NodeRole.Worker && n.ActiveTaskCount > 0)
               ?? CurrentState.Nodes.FirstOrDefault(n => n.Role == NodeRole.Worker && n.Health != NodeHealthStatus.Failed)
               ?? CurrentState.Nodes.FirstOrDefault(n => n.Role == NodeRole.Worker);
    }

    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(BreadcrumbText));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsInNodeOrModuleView));
        OnPropertyChanged(nameof(CanOpenSelectedNodeInternals));
        OnPropertyChanged(nameof(ActiveNode));
        OnPropertyChanged(nameof(SimulatedTimeDisplay));
        OnPropertyChanged(nameof(TargetWorkerActionLabel));
    }

    private void RefreshAllDerivedCollections()
    {
        RefreshClusterOutline();
        RefreshInspector();
        RefreshFilteredEvents();
        OnPropertyChanged(nameof(SimulatedTimeDisplay));
        OnPropertyChanged(nameof(HasChildModules));
        OnPropertyChanged(nameof(CanOpenSelectedNodeInternals));
        OnPropertyChanged(nameof(ActiveNode));
        OnPropertyChanged(nameof(TargetWorkerActionLabel));
        OnPropertyChanged(nameof(DelayActionLabel));
        OnPropertyChanged(nameof(DropActionLabel));
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
        InspectorChildModules.Clear();

        if (CurrentLevel == NavigationLevel.Module && FindModule(ActiveNodeId, SelectedModuleId) is { } module)
        {
            var parentNode = FindNode(module.NodeId);
            InspectorKindLabel = "INTERNAL MODULE";
            InspectorTitle = module.Name;
            InspectorSubtitle = $"{parentNode?.Name ?? module.NodeId} · {module.ShortRole}";
            InspectorStatusLabel = FormatModuleStatus(module.Status);
            InspectorPlainDescription = module.PlainDescription;
            InspectorQueueTitle = "Items & Local Queue";

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
            InspectorKindLabel = $"{node.Role.ToString().ToUpperInvariant()} MACHINE";
            InspectorTitle = node.Name;
            InspectorSubtitle = node.StatusSummary;
            InspectorStatusLabel = FormatNodeHealth(node.Health);
            InspectorPlainDescription = node.PlainDescription;
            InspectorQueueTitle = "Recent Inbox & Outbox Activity";

            foreach (var item in node.InboxItems)
            {
                InspectorQueueItems.Add($"In: {item}");
            }

            foreach (var item in node.OutboxItems)
            {
                InspectorQueueItems.Add($"Out: {item}");
            }

            if (InspectorQueueItems.Count == 0)
            {
                InspectorQueueItems.Add("No recent messages");
            }

            InspectorCounters.Add(new KeyValueItem("Active Tasks (Simulated)", $"{node.ActiveTaskCount} / {node.Capacity}"));
            InspectorCounters.Add(new KeyValueItem("Internal Modules", node.Modules.Count.ToString()));
            InspectorCounters.Add(new KeyValueItem("Simulated Clock", $"{CurrentState.SimulatedTimeMs} ms"));

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
            InspectorKindLabel = "CLUSTER OVERVIEW";
            InspectorTitle = CurrentState.ScenarioTitle;
            InspectorSubtitle = $"{CurrentState.Nodes.Count} machines · Seed #{CurrentState.Seed}";
            InspectorStatusLabel = metrics.RunningTasks > 0 ? "▶ Processing" : "● Healthy";
            InspectorPlainDescription = ActiveScenario.ShortSummary;
            InspectorQueueTitle = "Jobs & Tasks";

            if (CurrentState.Tasks.Count == 0)
            {
                InspectorQueueItems.Add("No tasks yet — press Play or Step");
            }
            else
            {
                foreach (var task in CurrentState.Tasks)
                {
                    var workerText = task.AssignedWorkerId is null ? "Queued" : FormatNodeName(task.AssignedWorkerId);
                    var attemptText = task.AcceptedAttemptId ?? task.CurrentAttemptId ?? "-";
                    InspectorQueueItems.Add($"{task.Id} ({task.Status}, {attemptText}) → {workerText}");
                }
            }

            InspectorCounters.Add(new KeyValueItem("Pending / Running / Done", $"{metrics.PendingTasks} / {metrics.RunningTasks} / {metrics.CompletedTasks}"));
            InspectorCounters.Add(new KeyValueItem("Queue Depth (Simulated)", metrics.QueueDepth.ToString()));
            InspectorCounters.Add(new KeyValueItem("Retries (Simulated)", metrics.RetryCount.ToString()));
            InspectorCounters.Add(new KeyValueItem("Stale Replies Ignored", metrics.StaleResultsIgnored.ToString()));
            InspectorCounters.Add(new KeyValueItem("Messages Sent / Dropped", $"{metrics.MessagesSent} / {metrics.MessagesDropped}"));
            if (metrics.LastJobDurationMs.HasValue)
            {
                InspectorCounters.Add(new KeyValueItem("Last Job Duration (Simulated)", $"{metrics.LastJobDurationMs.Value} ms"));
            }
        }

        OnPropertyChanged(nameof(HasChildModules));
        OnPropertyChanged(nameof(CanOpenSelectedNodeInternals));
    }

    private void RefreshFilteredEvents()
    {
        FilteredEvents.Clear();

        // Newest events appear at the top so live simulation updates are immediately readable
        foreach (var evt in CurrentState.EventLog.OrderByDescending(e => e.Sequence))
        {
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

            FilteredEvents.Add(new EventListItem(
                evt,
                $"{evt.TimestampMs} ms",
                route,
                evt.Category.ToString(),
                evt.Id == SelectedEventId));
        }
    }

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
