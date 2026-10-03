using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropicast.Station.Encoding;

namespace Tropicast.Station.App.ViewModels;

public sealed partial class BroadcastViewModel : ViewModelBase, IDisposable
{
    private readonly BroadcastController _controller;
    private readonly IBroadcastConfirmation _confirmation;
    private readonly ProfileEditorViewModel _profiles;
    private readonly AudioDevicesViewModel _audio;
    private bool _disposed;
    private bool _confirming;

    public BroadcastViewModel(BroadcastController controller, IBroadcastConfirmation confirmation,
        ProfileEditorViewModel profiles, AudioDevicesViewModel audio)
    {
        _controller = controller;
        _confirmation = confirmation;
        _profiles = profiles;
        _audio = audio;
        controller.Changed += OnChanged;
        profiles.PropertyChanged += OnSelectionChanged;
        audio.PropertyChanged += OnSelectionChanged;
    }

    [ObservableProperty] public partial BroadcastState State { get; set; } = BroadcastState.Idle;
    [ObservableProperty] public partial string Status { get; set; } = "Select a saved profile and audio source, then Go Live.";
    [ObservableProperty] public partial string Elapsed { get; set; } = "00:00:00";
    [ObservableProperty] public partial bool IsActive { get; set; }
    public string StateLabel => State.ToString();
    public string ActionLabel => IsActive ? "Stop broadcast" : "Go Live";
    public string TrayLabel => $"Tropicast Station — {StateLabel} ({Elapsed})";
    public bool HasActiveBroadcast => _controller.Snapshot.IsActive;
    private bool CanGoLive => !IsActive && !_disposed && !_confirming && !_profiles.IsBusy && !_audio.IsBusy
        && _profiles.SelectedProfile is not null && (_audio.SelectedInput ?? _audio.SelectedOutput) is not null;
    private bool CanStop => IsActive && State != BroadcastState.Stopping && !_confirming && !_disposed;

    [RelayCommand(CanExecute = nameof(CanGoLive))]
    private async Task GoLiveAsync()
    {
        var profile = _profiles.SelectedProfile!;
        var device = (_audio.SelectedInput ?? _audio.SelectedOutput)!;
        var options = new EncoderOptions(new(_audio.SampleRate, _audio.Channels));
        SetLocked(true);
        await _controller.StartAsync(profile.Id, device.Id, options);
        ApplySnapshot();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (await ConfirmAsync(false))
        {
            await _controller.StopAsync();
            ApplySnapshot();
        }
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (!_controller.Snapshot.IsActive)
        {
            return true;
        }
        if (!await ConfirmAsync(true))
        {
            return false;
        }
        await _controller.StopAsync();
        ApplySnapshot();
        return !_controller.Snapshot.IsActive;
    }

    private async Task<bool> ConfirmAsync(bool closing)
    {
        if (_confirming)
        {
            return false;
        }
        _confirming = true;
        NotifyCommands();
        try
        {
            if (_controller.Snapshot.State == BroadcastState.Connecting)
            {
                // No audio has gone live yet; cancellation does not need a live-stream warning.
                return true;
            }
            return await _confirmation.ConfirmStopAsync(closing);
        }
        finally
        {
            _confirming = false;
            NotifyCommands();
        }
    }

    private void SetLocked(bool locked)
    {
        _profiles.IsBroadcastLocked = locked;
        _audio.IsBroadcastLocked = locked;
    }

    private void OnChanged(object? sender, BroadcastChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplySnapshot();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplySnapshot);
        }
    }

    private void ApplySnapshot()
    {
        if (_disposed)
        {
            return;
        }
        var snapshot = _controller.Snapshot;
        State = snapshot.State;
        IsActive = snapshot.IsActive;
        Status = snapshot.Message;
        Elapsed = $"{(int)snapshot.Elapsed.TotalHours:00}:{snapshot.Elapsed.Minutes:00}:{snapshot.Elapsed.Seconds:00}";
        SetLocked(IsActive);
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(TrayLabel));
        NotifyCommands();
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e) => NotifyCommands();
    private void NotifyCommands()
    {
        GoLiveCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _controller.Changed -= OnChanged;
        _profiles.PropertyChanged -= OnSelectionChanged;
        _audio.PropertyChanged -= OnSelectionChanged;
        GC.SuppressFinalize(this);
    }
}
