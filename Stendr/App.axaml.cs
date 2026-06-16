using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Threading.Tasks;

namespace Stendr;

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
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splashWindow = new SplashWindow();
            splashWindow.Opened += async (_, _) =>
            {
                await Task.Delay(1200);

                var mainWindow = new MainWindow();
                mainWindow.Opened += (_, _) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        splashWindow.Close();
                        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    }, DispatcherPriority.Background);
                };

                desktop.MainWindow = mainWindow;
                mainWindow.Show();
            };

            desktop.MainWindow = splashWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
