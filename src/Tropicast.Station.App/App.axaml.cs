using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tropicast.Station.App.Views;

namespace Tropicast.Station.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Avalonia owns the application; OnExit disposes the tray and host.")]
public partial class App : Application
{
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(5);

    private IHost? _host;
    private BroadcastTray? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = AppHost.Create(desktop.Args ?? []);
            _host.Start();

            // The host traps SIGTERM/Ctrl+C; close the UI too so the process actually exits.
            _host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
                () => Dispatcher.UIThread.Post(() => desktop.Shutdown()));

            desktop.MainWindow = _host.Services.GetRequiredService<MainWindow>();
            _tray = new BroadcastTray(desktop.MainWindow, _host.Services.GetRequiredService<ViewModels.BroadcastViewModel>());
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource(HostShutdownTimeout);
        _tray?.Dispose();
        _tray = null;
        _host.StopAsync(cts.Token).GetAwaiter().GetResult();
        _host.Dispose();
        _host = null;
    }
}
