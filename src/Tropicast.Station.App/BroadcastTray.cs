using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.Encoding;

namespace Tropicast.Station.App;

internal sealed class BroadcastTray : IDisposable
{
    private readonly BroadcastViewModel _model;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _state = new() { IsEnabled = false };
    private readonly NativeMenuItem _stop = new("Stop broadcast");
    private readonly WindowIcon _idleIcon;
    private readonly WindowIcon _liveIcon;

    internal BroadcastTray(Window window, BroadcastViewModel model)
    {
        _model = model;
        _idleIcon = new(AssetLoader.Open(new Uri("avares://Tropicast.Station/Assets/tropicast.ico")));
        _liveIcon = new(AssetLoader.Open(new Uri("avares://Tropicast.Station/Assets/tropicast-live.png")));
        var show = new NativeMenuItem("Show Tropicast Station");
        show.Click += (_, _) =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        };
        _stop.Command = model.StopCommand;
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => window.Close();
        var menu = new NativeMenu();
        menu.Items.Add(_state);
        menu.Items.Add(show);
        menu.Items.Add(_stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        _tray = new TrayIcon { Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        };
        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, new TrayIcons { _tray });
        }
        model.PropertyChanged += OnChanged;
        Update();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        _tray.ToolTipText = _model.TrayLabel;
        _state.Header = $"{_model.StateLabel} — {_model.Elapsed}";
        _stop.IsEnabled = _model.StopCommand.CanExecute(null);
        _tray.Icon = _model.State is BroadcastState.Live or BroadcastState.Reconnecting ? _liveIcon : _idleIcon;
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnChanged;
        _tray.Dispose();
        GC.SuppressFinalize(this);
    }
}
