using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WUWAScope.App.ViewModels;
using WUWAScope.App.Views;
using WUWAScope.Simulation.Stubs;

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
            var engine = new PrototypeStubEngine();
            var vm = new MainWindowViewModel(engine);

            if (desktop.Args is { Length: >= 2 } args &&
                string.Equals(args[0], "--open-node", StringComparison.OrdinalIgnoreCase))
            {
                vm.OpenNodeCommand.Execute(args[1]);
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
