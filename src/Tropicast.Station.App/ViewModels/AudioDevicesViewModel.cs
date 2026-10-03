using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropicast.Station.Audio;

namespace Tropicast.Station.App.ViewModels;

public sealed partial class AudioDevicesViewModel : ViewModelBase, IDisposable
{
    private readonly AudioCaptureService _capture;
    private bool _disposed;

    public AudioDevicesViewModel(AudioCaptureService capture)
    {
        _capture = capture;
        capture.Changed += OnCaptureChanged;
    }

    public string ProviderDescription => _capture.ProviderDescription;
    public ObservableCollection<AudioDevice> Inputs { get; } = [];
    public ObservableCollection<AudioDevice> Outputs { get; } = [];
    public IReadOnlyList<int> SampleRates { get; } = [44100, 48000];
    public IReadOnlyList<int> ChannelCounts { get; } = [1, 2];

    [ObservableProperty] public partial AudioDevice? SelectedInput { get; set; }
    [ObservableProperty] public partial AudioDevice? SelectedOutput { get; set; }
    [ObservableProperty] public partial int SampleRate { get; set; } = 48000;
    [ObservableProperty] public partial int Channels { get; set; } = 2;
    [ObservableProperty] public partial bool IsCapturing { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsBroadcastLocked { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Select a source, then start preview.";

    public bool CanChoose => !IsBusy && !IsCapturing && !IsBroadcastLocked && !_disposed;
    private bool CanStart => CanChoose && (SelectedInput ?? SelectedOutput) is not null
        && SampleRates.Contains(SampleRate) && ChannelCounts.Contains(Channels);
    private bool CanStop => !IsBusy && IsCapturing && !IsBroadcastLocked && !_disposed;
    private bool CanRefresh => !IsBusy && !IsBroadcastLocked && !_disposed;

    partial void OnSelectedInputChanged(AudioDevice? value)
    {
        if (value is not null)
        {
            SelectedOutput = null;
        }
    }

    partial void OnSelectedOutputChanged(AudioDevice? value)
    {
        if (value is not null)
        {
            SelectedInput = null;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(IsCapturing) or nameof(IsBroadcastLocked) or nameof(SelectedInput) or nameof(SelectedOutput)
            or nameof(SampleRate) or nameof(Channels))
        {
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanChoose));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await _capture.RefreshAsync();
            ApplySnapshot();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            await _capture.StartAsync((SelectedInput ?? SelectedOutput)!.Id, new(SampleRate, Channels));
            ApplySnapshot();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            await _capture.StopAsync();
            ApplySnapshot();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnCaptureChanged(object? sender, AudioSnapshotEventArgs e)
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

        var snapshot = _capture.Snapshot;
        var inputId = SelectedInput?.Id;
        var outputId = SelectedOutput?.Id;
        // Preserve selection by stable ID when names/defaults change. Never choose a fallback on removal.
        Replace(Inputs, snapshot.Devices.Where(d => d.Kind == AudioDeviceKind.Input));
        Replace(Outputs, snapshot.Devices.Where(d => d.Kind == AudioDeviceKind.Loopback));
        SelectedInput = Inputs.FirstOrDefault(d => d.Id == inputId);
        SelectedOutput = Outputs.FirstOrDefault(d => d.Id == outputId);
        IsCapturing = snapshot.IsCapturing;
        Status = IsBroadcastLocked && snapshot.IsCapturing ? "Audio capture is feeding the broadcast encoder." : snapshot.Message;
    }

    private static void Replace(ObservableCollection<AudioDevice> collection, IEnumerable<AudioDevice> devices)
    {
        var sorted = devices.OrderByDescending(d => d.IsDefault).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (collection.SequenceEqual(sorted))
        {
            return;
        }

        collection.Clear();
        foreach (var device in sorted)
        {
            collection.Add(device);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _capture.Changed -= OnCaptureChanged;
        GC.SuppressFinalize(this);
    }
}
