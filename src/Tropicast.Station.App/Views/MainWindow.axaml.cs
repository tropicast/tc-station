using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Tropicast.Station.App.ViewModels;

namespace Tropicast.Station.App.Views;

public partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _checkingClose;
    private WindowNotificationManager? _notifications;
    public MainWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainViewModel model)
        {
            _notifications = new(this) { Position = NotificationPosition.TopRight, MaxItems = 1 };
            model.Audio.Levels.SilenceStarted += OnSilenceStarted;
            model.Profiles.LoadCommand.Execute(null);
            model.Audio.RefreshCommand.Execute(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.Audio.Levels.SilenceStarted -= OnSilenceStarted;
            model.Profiles.TestCommand.Cancel();
            model.Profiles.Password = "";
            if (model.Audio.StopCommand.CanExecute(null))
            {
                model.Audio.StopCommand.Execute(null);
            }
        }

        base.OnClosed(e);
    }

    private void OnSilenceStarted(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            _notifications?.Show(new Notification("Audio silence", model.Audio.Levels.Status,
                NotificationType.Warning, TimeSpan.FromSeconds(5)));
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeApproved && DataContext is MainViewModel model && model.Broadcast.HasActiveBroadcast)
        {
            e.Cancel = true;
            base.OnClosing(e);
            if (_checkingClose)
            {
                return;
            }
            _checkingClose = true;
            try
            {
                if (await model.Broadcast.RequestCloseAsync())
                {
                    _closeApproved = true;
                    Close();
                }
            }
            finally
            {
                _checkingClose = false;
            }
            return;
        }
        base.OnClosing(e);
    }
}
