using Avalonia;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;
using Velopack;

namespace Stndr;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        InstallCrashLogging();

        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static void InstallCrashLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                CrashLogService.WriteCrashLog(exception, "AppDomain.CurrentDomain.UnhandledException", e.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogService.WriteCrashLog(e.Exception, "TaskScheduler.UnobservedTaskException", isTerminating: false);
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLogService.WriteCrashLog(e.Exception, "Dispatcher.UIThread.UnhandledException", isTerminating: false);
        };
    }
}
