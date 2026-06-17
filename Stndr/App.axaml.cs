using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace Stndr;

public partial class App : Application
{
    private static readonly TimeSpan MinimumSplashDisplayTime = TimeSpan.FromMilliseconds(1200);

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
            splashWindow.Topmost = true;
            splashWindow.Opened += async (_, _) =>
            {
                var minimumSplashDelay = Task.Delay(MinimumSplashDisplayTime);

                var mainWindow = await Dispatcher.UIThread.InvokeAsync(
                    () => new MainWindow(),
                    DispatcherPriority.Background);
                mainWindow.StartupCompleted += (_, _) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        splashWindow.Topmost = false;
                        splashWindow.Close();
                        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    }, DispatcherPriority.Background);
                };

                await minimumSplashDelay;

                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                Dispatcher.UIThread.Post(() =>
                {
                    if (splashWindow.IsVisible)
                    {
                        splashWindow.Activate();
                    }
                }, DispatcherPriority.Loaded);
            };

            desktop.MainWindow = splashWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
