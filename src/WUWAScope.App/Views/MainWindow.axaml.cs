using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WUWAScope.App.ViewModels;

namespace WUWAScope.App.Views;

public partial class MainWindow : Window
{
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
