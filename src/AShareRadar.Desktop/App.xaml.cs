using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using AShareRadar.Desktop.Services;
using NLog;

namespace AShareRadar.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        ThemeService.Apply(ThemeService.LoadSavedTheme(), save: false);
        base.OnStartup(e);
        Logger.Info(
            "Desktop started. Version={Version} BaseDirectory={BaseDirectory} ProcessId={ProcessId} OS={OS} Runtime={Runtime}",
            typeof(App).Assembly.GetName().Version,
            AppContext.BaseDirectory,
            Environment.ProcessId,
            Environment.OSVersion,
            Environment.Version);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("Desktop stopping. ExitCode={ExitCode}", e.ApplicationExitCode);
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        LogManager.Shutdown();
        base.OnExit(e);
    }

    public static void LogException(Exception exception, string message) => Logger.Error(exception, message);

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Fatal(e.Exception, "Unhandled WPF dispatcher exception.");
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logger.Fatal(e.ExceptionObject as Exception, "Unhandled application domain exception. IsTerminating={IsTerminating}", e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error(e.Exception, "Unobserved task exception.");
    }
}

