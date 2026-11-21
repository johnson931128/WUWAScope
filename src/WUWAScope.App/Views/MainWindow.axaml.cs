using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WUWAScope.App.ViewModels;

namespace WUWAScope.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private double _lastFrameSeconds;

    public MainWindow()
    {
        InitializeComponent();

        TopologyCanvas.NodeSelected += nodeId =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectNodeCommand.Execute(nodeId);
            }
        };

        TopologyCanvas.NodeDoubleClicked += nodeId =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OpenNodeCommand.Execute(nodeId);
            }
        };

        TopologyCanvas.ModuleSelected += moduleId =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectModuleCommand.Execute(moduleId);
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.LayoutResetRequested += () => TopologyCanvas.ResetCustomLayout();
            }
        };

        // 60 FPS UI clock strictly driving visual interpolation & calling engine.Step() at exact simulated timestamps
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _playbackTimer.Tick += OnPlaybackTick;
        _playbackTimer.Start();

        Closed += (_, _) => _playbackTimer.Stop();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        var nowSec = _frameClock.Elapsed.TotalSeconds;
        var dt = Math.Max(0, nowSec - _lastFrameSeconds);
        _lastFrameSeconds = nowSec;

        if (DataContext is MainWindowViewModel vm && vm.IsPlaying)
        {
            vm.AdvancePlaybackFrame(dt);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Ignore single-key shortcuts while typing in a TextBox
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        if (e.Key == Key.Escape && vm.CanGoBack)
        {
            vm.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && vm.CanRunLivePlayback)
        {
            vm.TogglePlayPauseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.None && vm.CanRunLivePlayback)
        {
            vm.StepSimulationCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.None)
        {
            vm.ResetSimulationCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => TopologyCanvas.ZoomIn();

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => TopologyCanvas.ZoomOut();

    private void OnFitViewClicked(object? sender, RoutedEventArgs e) => TopologyCanvas.FitToView();

    private void OnResetLayoutClicked(object? sender, RoutedEventArgs e) => TopologyCanvas.ResetCustomLayout();
}
