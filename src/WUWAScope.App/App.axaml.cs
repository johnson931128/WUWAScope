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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(engine)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
