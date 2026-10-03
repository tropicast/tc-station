namespace Tropicast.Station.Audio.Windows.Tests;

internal sealed class FakeBackend : IWasapiBackend
{
    public IReadOnlyList<AudioDevice> Devices { get; set; } =
    [
        new("usb-mixer", "USB mixer", AudioDeviceKind.Input, true, new(48000, 2)),
        new("speakers", "Speakers", AudioDeviceKind.Loopback, true, new(44100, 2, PcmEncoding.Signed16)),
    ];
    public FakeSource Source { get; } = new();
    public bool Denied { get; set; }
    public bool Disposed { get; private set; }
    public bool OpenedWithSynchronizationContext { get; private set; }
    public event EventHandler? DevicesChanged;
    public IReadOnlyList<AudioDevice> GetDevices() => Devices;
    public bool IsMicrophoneDenied() => Denied;
    public IWasapiSource Open(string deviceId)
    {
        OpenedWithSynchronizationContext = SynchronizationContext.Current is not null;
        Source.Format = Devices.Single(d => d.Id == deviceId).NativeFormat;
        return Source;
    }
    public void Notify() => DevicesChanged?.Invoke(this, EventArgs.Empty);
    public void Dispose() => Disposed = true;
}

internal sealed class FakeSource : IWasapiSource
{
    public AudioFormat Format { get; set; } = new(48000, 2);
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public int Disposes { get; private set; }
    public Exception? StartError { get; init; }
    public event EventHandler<WasapiDataEventArgs>? DataAvailable;
    public event EventHandler<WasapiStoppedEventArgs>? Stopped;
    public void Start()
    {
        Starts++;
        if (StartError is not null)
        {
            throw StartError;
        }
    }
    public void Stop() => Stops++;
    public void Emit(byte[] bytes, int? count = null) => DataAvailable?.Invoke(this, new(bytes, count ?? bytes.Length));
    public void End(Exception? error = null) => Stopped?.Invoke(this, new(error));
    public void Dispose() => Disposes++;
}
