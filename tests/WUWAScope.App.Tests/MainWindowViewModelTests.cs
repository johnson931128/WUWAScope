using WUWAScope.App.ViewModels;
using WUWAScope.Simulation.Engine;
using WUWAScope.Simulation.Models;
using WUWAScope.Simulation.Stubs;

namespace WUWAScope.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void InitialState_LoadsFiveMachinesAndMarksPrototypeStubHonestly()
    {
        var vm = new MainWindowViewModel(new PrototypeStubEngine());

        Assert.True(vm.IsPrototypeStub);
        Assert.False(vm.CanRunLivePlayback);
        Assert.Equal(NavigationLevel.Cluster, vm.CurrentLevel);
        Assert.Equal(5, vm.ClusterNodes.Count);
        Assert.Equal("coordinator", vm.SelectedNodeId);
        Assert.Equal("Coordinator", vm.InspectorTitle);
    }

    [Fact]
    public void DrillDownNavigation_ClusterToNodeToModuleAndBack_PreservesSimulationState()
    {
        var vm = new MainWindowViewModel(new PrototypeStubEngine());
        var initialTime = vm.CurrentState.SimulatedTimeMs;

        // Cluster -> Node
        vm.OpenNodeCommand.Execute("coordinator");
        Assert.Equal(NavigationLevel.Node, vm.CurrentLevel);
        Assert.Equal("coordinator", vm.ActiveNodeId);
        Assert.True(vm.CanGoBack);
        Assert.Equal("Cluster / Coordinator", vm.BreadcrumbText);
        Assert.True(vm.HasChildModules);

        // Node -> Module 1
        vm.SelectModuleCommand.Execute("coord-job-queue");
        Assert.Equal(NavigationLevel.Module, vm.CurrentLevel);
        Assert.Equal("Job Queue", vm.InspectorTitle);
        var queueDesc = vm.InspectorPlainDescription;

        // Switch to Module 2 -> contextual details must differ
        vm.SelectModuleCommand.Execute("coord-scheduler");
        Assert.Equal(NavigationLevel.Module, vm.CurrentLevel);
        Assert.Equal("Scheduler", vm.InspectorTitle);
        Assert.NotEqual(queueDesc, vm.InspectorPlainDescription);

        // Step back Module -> Node -> Cluster
        vm.GoBackCommand.Execute(null);
        Assert.Equal(NavigationLevel.Node, vm.CurrentLevel);
        Assert.Equal("coordinator", vm.ActiveNodeId);

        vm.GoBackCommand.Execute(null);
        Assert.Equal(NavigationLevel.Cluster, vm.CurrentLevel);
        Assert.Null(vm.ActiveNodeId);
        Assert.Equal(initialTime, vm.CurrentState.SimulatedTimeMs);
    }

    [Fact]
    public void CollapsiblePanelsAndTwoTabInspector_ToggleCleanly()
    {
        var vm = new MainWindowViewModel(new PrototypeStubEngine());

        Assert.True(vm.IsLeftSidebarOpen);
        Assert.True(vm.IsRightInspectorOpen);
        Assert.True(vm.IsBottomTimelineOpen);

        vm.ToggleLeftSidebarCommand.Execute(null);
        vm.ToggleRightInspectorCommand.Execute(null);
        vm.ToggleBottomTimelineCommand.Execute(null);

        Assert.False(vm.IsLeftSidebarOpen);
        Assert.False(vm.IsRightInspectorOpen);
        Assert.False(vm.IsBottomTimelineOpen);

        Assert.True(vm.IsStatusTabSelected);
        vm.ShowExplainTabCommand.Execute(null);
        Assert.True(vm.IsExplainTabSelected);
        Assert.False(vm.IsStatusTabSelected);
    }

    [Fact]
    public void TimelineFilteringAndSelection_FilterEventsAndUpdateInspector()
    {
        var vm = new MainWindowViewModel(new PrototypeStubEngine());
        var totalCount = vm.FilteredEvents.Count;
        Assert.True(totalCount >= 4);

        vm.SetEventCategoryFilterCommand.Execute("Heartbeats");
        Assert.Single(vm.FilteredEvents);
        Assert.Equal("Heartbeats", vm.FilteredEvents[0].CategoryDisplay);

        var hbEvent = vm.FilteredEvents[0];
        vm.SelectEventCommand.Execute(hbEvent);

        Assert.True(vm.HasSelectedEvent);
        Assert.Equal(hbEvent.Record.Id, vm.SelectedEventId);
        Assert.Equal("worker-1", vm.SelectedNodeId);
        Assert.NotEmpty(vm.SelectedEventPayload);
    }

    [Fact]
    public void ExplorerTree_ExpandAndCollapseNodes_WorksIndependently()
    {
        var vm = new MainWindowViewModel(new PrototypeStubEngine());

        var coordInitial = Assert.Single(vm.ClusterNodes, n => n.Id == "coordinator");
        var workerInitial = Assert.Single(vm.ClusterNodes, n => n.Id == "worker-1");
        Assert.True(coordInitial.IsExpanded);
        Assert.False(workerInitial.IsExpanded);

        vm.ToggleNodeExpandedCommand.Execute("coordinator");
        vm.ToggleNodeExpandedCommand.Execute("worker-1");

        var coordAfter = Assert.Single(vm.ClusterNodes, n => n.Id == "coordinator");
        var workerAfter = Assert.Single(vm.ClusterNodes, n => n.Id == "worker-1");
        Assert.False(coordAfter.IsExpanded);
        Assert.Equal("▸", coordAfter.ChevronIcon);
        Assert.True(workerAfter.IsExpanded);
        Assert.Equal("▾", workerAfter.ChevronIcon);
    }

    [Fact]
    public void LiveEngine_PauseBlocksProgressionAndStepAdvancesExactlyOneEvent()
    {
        var vm = new MainWindowViewModel(new DiscreteEventSimulationEngine(seed: 42));

        Assert.False(vm.IsPrototypeStub);
        Assert.True(vm.CanRunLivePlayback);
        Assert.True(vm.CanPlay);
        Assert.False(vm.CanPause);
        Assert.Empty(vm.CurrentState.EventLog);

        // Pausing (not playing) must block time advancement completely
        vm.AdvancePlaybackFrame(0.5);
        Assert.Equal(0, vm.VisualTimeMs);
        Assert.Empty(vm.CurrentState.EventLog);

        // Stepping advances exactly one event
        vm.StepSimulationCommand.Execute(null);
        Assert.Single(vm.CurrentState.EventLog);
        Assert.Equal(SimulationEventType.JobSubmitted, vm.CurrentState.EventLog[0].Type);

        // Playing advances visual time smoothly and completes job end-to-end
        vm.PlaySimulationCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.True(vm.CanPause);

        for (var i = 0; i < 120; i++)
        {
            vm.AdvancePlaybackFrame(0.05);
            if (vm.CurrentState.EventLog.Any(e => e.Type == SimulationEventType.ClientResponded))
            {
                break;
            }
        }

        Assert.Contains(vm.CurrentState.EventLog, e => e.Type == SimulationEventType.ClientResponded);

        // Pause freezes time immediately
        vm.PauseSimulationCommand.Execute(null);
        var frozenTime = vm.VisualTimeMs;
        var frozenCount = vm.CurrentState.EventLog.Count;
        vm.AdvancePlaybackFrame(0.5);
        Assert.Equal(frozenTime, vm.VisualTimeMs);
        Assert.Equal(frozenCount, vm.CurrentState.EventLog.Count);
    }

    [Fact]
    public void LiveEngine_FaultControlsAndReset_UpdateStateAndButtonLabels()
    {
        var vm = new MainWindowViewModel(new DiscreteEventSimulationEngine(seed: 42));

        // Step until Worker 1 starts running Task T1
        for (var i = 0; i < 20; i++)
        {
            vm.StepSimulationCommand.Execute(null);
            if (vm.CurrentState.EventLog.Last().Type == SimulationEventType.TaskStarted)
            {
                break;
            }
        }

        Assert.Contains("Crash Worker 1", vm.TargetWorkerActionLabel);

        vm.ToggleCrashTargetWorkerCommand.Execute(null);
        var w1 = Assert.Single(vm.CurrentState.Nodes, n => n.Id == "worker-1");
        Assert.Equal(NodeHealthStatus.Failed, w1.Health);

        vm.SelectNodeCommand.Execute("worker-1");
        Assert.Contains("Recover Worker 1", vm.TargetWorkerActionLabel);

        vm.DelayNextMessageCommand.Execute(null);
        Assert.Contains("(1)", vm.DelayActionLabel);

        vm.DropNextMessageCommand.Execute(null);
        Assert.Contains("(1)", vm.DropActionLabel);

        // Reset restores clean initial state
        vm.ResetSimulationCommand.Execute(null);
        Assert.False(vm.IsPlaying);
        Assert.Equal(0, vm.VisualTimeMs);
        Assert.Empty(vm.CurrentState.EventLog);
        Assert.Equal(0, vm.CurrentState.Metrics.ArmedDelayCount);
        Assert.Equal(0, vm.CurrentState.Metrics.ArmedDropCount);
    }

    [Fact]
    public void ScenarioSwitching_LoadsAllFourLessonsWithCorrectTopologyAndActions()
    {
        var vm = new MainWindowViewModel(new DiscreteEventSimulationEngine(seed: 42));
        Assert.Equal("task-scheduling", vm.CurrentState.ScenarioId);
        Assert.Equal(5, vm.ClusterNodes.Count);

        // Switch to Logical Clocks (3 processes)
        vm.SelectScenarioCommand.Execute("logical-clocks");
        Assert.Equal("logical-clocks", vm.CurrentState.ScenarioId);
        Assert.Equal(3, vm.ClusterNodes.Count);
        Assert.Contains("Local Event", vm.Action1Label);
        Assert.False(vm.ShowAction4);
        vm.StepSimulationCommand.Execute(null);
        Assert.Single(vm.CurrentState.EventLog);

        // Switch to Global Snapshots (3 accounts)
        vm.SelectScenarioCommand.Execute("global-snapshots");
        Assert.Equal("global-snapshots", vm.CurrentState.ScenarioId);
        Assert.Equal(3, vm.ClusterNodes.Count);
        Assert.Contains("Snapshot", vm.Action1Label);
        vm.StepSimulationCommand.Execute(null);
        Assert.Single(vm.CurrentState.EventLog);

        // Switch to Raft Leader Election (5 servers)
        vm.SelectScenarioCommand.Execute("raft-leader-election");
        Assert.Equal("raft-leader-election", vm.CurrentState.ScenarioId);
        Assert.Equal(5, vm.ClusterNodes.Count);
        Assert.True(vm.ShowAction4);
        vm.StepSimulationCommand.Execute(null);
        Assert.Single(vm.CurrentState.EventLog);
    }

    [Fact]
    public void PlanBStoryFlow_HidesHeartbeatsByDefaultAndShowsFriendlyTags()
    {
        var vm = new MainWindowViewModel(new PrototypeStubEngine());

        // Heartbeats hidden by default so key story events stand out
        Assert.True(vm.HideHeartbeats);
        Assert.DoesNotContain(vm.FilteredEvents, e => e.CategoryDisplay == "Heartbeats");
        Assert.All(vm.FilteredEvents, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.FriendlyTag));
            Assert.StartsWith("#", e.DotColorHex);
        });

        // Toggling Heartbeats on reveals them
        vm.ToggleHideHeartbeatsCommand.Execute(null);
        Assert.False(vm.HideHeartbeats);
        Assert.Contains(vm.FilteredEvents, e => e.CategoryDisplay == "Heartbeats");
    }

    [Fact]
    public void NodeInfoTab_ShowsRoleSpecificNonRedundantContent()
    {
        var vm = new MainWindowViewModel(new DiscreteEventSimulationEngine(seed: 42));

        // Coordinator shows Waiting Queue + Workers Status + non-redundant counters (no Sim Clock or Modules)
        vm.SelectNodeCommand.Execute("coordinator");
        Assert.StartsWith("WAITING QUEUE", vm.InspectorQueueTitle);
        Assert.True(vm.HasSecondarySection);
        Assert.Equal("WORKERS STATUS", vm.InspectorSecondaryTitle);
        Assert.Equal(3, vm.InspectorSecondaryItems.Count);
        Assert.DoesNotContain(vm.InspectorCounters, c => c.Label.Contains("Clock", StringComparison.OrdinalIgnoreCase));

        // Worker shows Current Work without secondary Workers Status table
        vm.SelectNodeCommand.Execute("worker-1");
        Assert.Equal("CURRENT WORK", vm.InspectorQueueTitle);
        Assert.False(vm.HasSecondarySection);
        Assert.NotEmpty(vm.InspectorCounters);
    }
}
