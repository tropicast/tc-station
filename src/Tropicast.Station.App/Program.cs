using Avalonia;

namespace Tropicast.Station.App;

internal static class Program
{
    // Avalonia, third-party APIs and SynchronizationContext-dependent code must not run before AppMain.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error)
        {
            return UnhandledErrors.ReportStartupFailure(error);
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont();
}
