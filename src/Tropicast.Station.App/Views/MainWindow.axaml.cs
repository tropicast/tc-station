using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Tropicast.Station.App.ViewModels;

namespace Tropicast.Station.App.Views;

public partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _checkingClose;
    private WindowNotificationManager? _notifications;
    internal Diagnostics? Diagnostics { get; set; }
    public MainWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        this.FindControl<TextBlock>("LogDirectory")!.Text = Diagnostics?.LogDirectory ?? "Logs are not initialized.";
        if (DataContext is MainViewModel model)
        {
            _notifications = new(this) { Position = NotificationPosition.TopRight, MaxItems = 1 };
            model.Audio.Levels.SilenceStarted += OnSilenceStarted;
            model.Account.LoadCommand.Execute(null);
            model.Profiles.LoadCommand.Execute(null);
            model.Audio.RefreshCommand.Execute(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.Audio.Levels.SilenceStarted -= OnSilenceStarted;
            model.Account.SignInCommand.Cancel();
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

    private async void OnExportDiagnostics(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("DiagnosticStatus")!;
        var button = this.FindControl<Button>("ExportDiagnosticsButton")!;
        button.IsEnabled = false;
        try
        {
            if (Diagnostics is null)
            {
                throw new InvalidOperationException("Diagnostic export is not initialized.");
            }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export safe diagnostics", SuggestedFileName = $"tropicast-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("ZIP diagnostics") { Patterns = ["*.zip"] }],
            });
            if (file is null)
            {
                status.Text = "Export cancelled.";
                return;
            }
            using (file)
            {
                // Assemble before opening the destination so corrupt logs do not produce a partial bundle.
                using var bundle = new MemoryStream();
                await Diagnostics.ExportAsync(bundle);
                bundle.Position = 0;
                await using var output = await file.OpenWriteAsync();
                output.SetLength(0);
                await bundle.CopyToAsync(output);
            }
            status.Text = Diagnostics.HasLogFailure
                ? "Diagnostics exported, but logging has failed. Check the log directory permissions and free space."
                : "Diagnostics exported. No credentials or profile/device names are included.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Diagnostics?.ReportExportFailure(error);
            status.Text = "Could not export diagnostics. Check the destination and log directory permissions and free space.";
        }
        finally
        {
            button.IsEnabled = true;
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
