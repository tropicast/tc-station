using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Tropicast.Station.Encoding;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.App;

internal sealed partial class UnhandledErrors(BroadcastController broadcast, ILogger<Diagnostics> logger) : IDisposable
{
    private bool _attached;
    private int _reporting;
    internal Window? Owner { get; set; }
    internal Action? Shutdown { get; set; }

    internal void Attach()
    {
        if (_attached)
        {
            return;
        }
        _attached = true;
        Dispatcher.UIThread.UnhandledException += OnDispatcherError;
        TaskScheduler.UnobservedTaskException += OnTaskError;
        AppDomain.CurrentDomain.UnhandledException += OnDomainError;
    }

    private void OnDispatcherError(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = ReportAsync(e.Exception);
    }

    private void OnTaskError(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Record(e.Exception);
        Dispatcher.UIThread.Post(() => _ = ReportAsync(e.Exception));
    }

    private void OnDomainError(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Record(exception);
        }
        Console.Error.WriteLine("Tropicast encountered a fatal error. Restart the app and export diagnostics for support.");
        // Prevent the runtime from printing credential-bearing exception text after this handler.
        Environment.Exit(1);
    }

    internal async Task ReportAsync(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.Exchange(ref _reporting, 1) != 0)
        {
            return;
        }
        Record(error);
        try
        {
            await broadcast.StopAsync();
        }
        catch (Exception cleanupError)
        {
            // This is the final safety boundary: record only its type, then require process exit.
            Record(cleanupError);
        }
        try
        {
            if (Owner is { IsVisible: true } owner)
            {
                owner.WindowState = WindowState.Normal;
                owner.Activate();
                var dialog = new Window
                {
                    Title = "Tropicast Station error", Width = 480, Height = 220, CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                var quit = new Button { Content = "Quit", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
                quit.Click += (_, _) => dialog.Close();
                dialog.Content = new StackPanel
                {
                    Margin = new Thickness(20), Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "An unexpected error occurred. Broadcasting has been stopped where possible. Quit and restart Tropicast Station. Recent diagnostic logs can help support; no credentials are included.",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                        quit,
                    },
                };
                dialog.Opened += (_, _) => quit.Focus();
                await dialog.ShowDialog(owner);
            }
        }
        catch (Exception dialogError)
        {
            Record(dialogError);
            Console.Error.WriteLine("Tropicast could not display its error dialog. Restart the app and export diagnostics for support.");
        }
        finally
        {
            Shutdown?.Invoke();
        }
    }

    internal static int ReportStartupFailure(Exception error)
    {
        using var logs = new SafeLogProvider(SafeLogProvider.DefaultDirectory, Console.Error);
        var startupLogger = logs.CreateLogger("Tropicast.Station.App.Diagnostics");
        if (startupLogger.IsEnabled(LogLevel.Critical))
        {
            var type = error.GetType().Name;
            LogUnhandled(startupLogger, type);
        }
        Console.Error.WriteLine("Tropicast Station could not start. Check the user diagnostic logs, then restart the app.");
        return 1;
    }

    public void Dispose()
    {
        if (_attached)
        {
            Dispatcher.UIThread.UnhandledException -= OnDispatcherError;
            TaskScheduler.UnobservedTaskException -= OnTaskError;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainError;
            _attached = false;
        }
        GC.SuppressFinalize(this);
    }

    private void Record(Exception error)
    {
        if (logger.IsEnabled(LogLevel.Critical))
        {
            var type = error.GetType().Name;
            LogUnhandled(logger, type);
        }
    }

    [LoggerMessage(EventId = 1303, Level = LogLevel.Critical, Message = "Unhandled error ({ErrorType}); restart required.")]
    private static partial void LogUnhandled(ILogger logger, string errorType);
}
