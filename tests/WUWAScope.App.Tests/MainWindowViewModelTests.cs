using WUWAScope.App.ViewModels;
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
        Assert.Equal("Cluster  /  Coordinator", vm.BreadcrumbText);
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
}
