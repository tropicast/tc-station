using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Tropicast.Station.Audio;

namespace Tropicast.Station.App.ViewModels;

public sealed partial class ChannelLevelViewModel(string name) : ViewModelBase
{
    public string Name { get; } = name;
    public string PeakName => $"{Name} peak level in dBFS";
    public string RmsName => $"{Name} RMS level in dBFS";
    [ObservableProperty] public partial double Peak { get; set; } = AudioLevelMeter.FloorDb;
    [ObservableProperty] public partial double Rms { get; set; } = AudioLevelMeter.FloorDb;
    [ObservableProperty] public partial bool IsClipping { get; set; }
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Name}: peak {Peak:0.0}, RMS {Rms:0.0} dBFS");

    internal void Apply(ChannelLevel level)
    {
        Peak = level.PeakDb;
        Rms = level.RmsDb;
        IsClipping = level.IsClipping;
        OnPropertyChanged(nameof(Label));
    }
}

public sealed partial class AudioLevelsViewModel : ViewModelBase, IDisposable
{
    private readonly AudioLevelMeter _meter;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public AudioLevelsViewModel(AudioCaptureService capture)
    {
        _meter = capture.Levels;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public ObservableCollection<ChannelLevelViewModel> ChannelLevels { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] public partial bool IsActive { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] public partial bool IsSilent { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] public partial decimal SilenceThreshold { get; set; } = -50;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status))] public partial decimal SilenceSeconds { get; set; } = 5;
    [ObservableProperty] public partial bool NotifyOnSilence { get; set; }
    public string Status => !IsActive ? "Start preview or Go Live to see audio levels." : IsSilent
        ? string.Create(CultureInfo.InvariantCulture, $"Silence warning: all channels below {SilenceThreshold:0} dBFS for {SilenceSeconds:0} seconds.")
        : "Monitoring normalized audio (dBFS).";
    public event EventHandler? SilenceStarted;

    partial void OnSilenceThresholdChanged(decimal value) => Configure();
    partial void OnSilenceSecondsChanged(decimal value) => Configure();

    private void Configure()
    {
        _meter.Configure((double)SilenceThreshold, TimeSpan.FromSeconds((double)SilenceSeconds));
        Refresh();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        var snapshot = _meter.Read();
        var wasSilent = IsSilent;
        IsActive = snapshot.IsActive;
        IsSilent = snapshot.IsSilent;
        if (ChannelLevels.Count != snapshot.Channels.Count)
        {
            ChannelLevels.Clear();
            for (var i = 0; i < snapshot.Channels.Count; i++)
            {
                ChannelLevels.Add(new(snapshot.Channels.Count == 1 ? "Mono" : i == 0 ? "Left" : "Right"));
            }
        }
        for (var i = 0; i < snapshot.Channels.Count; i++)
        {
            ChannelLevels[i].Apply(snapshot.Channels[i]);
        }
        if (!wasSilent && IsSilent && NotifyOnSilence)
        {
            SilenceStarted?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        GC.SuppressFinalize(this);
    }
}
