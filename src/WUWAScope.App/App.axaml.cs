using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WUWAScope.App.ViewModels;
using WUWAScope.App.Views;
using WUWAScope.Simulation.Engine;

namespace WUWAScope.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var engine = new DiscreteEventSimulationEngine();
            var vm = new MainWindowViewModel(engine);

            if (desktop.Args is { Length: > 0 } args)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--open-node", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        vm.OpenNodeCommand.Execute(args[i + 1]);
                    }
                    else if (string.Equals(args[i], "--autoplay", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.PlaySimulationCommand.Execute(null);
                    }
                }
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
