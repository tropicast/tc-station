using Microsoft.Extensions.Logging.Abstractions;

namespace Tropicast.Station.Audio.MacOS.Tests;

public sealed class MacDeviceTests
{
    private const string Devices = """
        [
          {"id":"built-in","name":"Built-in microphone","rate":48000,"channels":1,"default":true,"loopback":false},
          {"id":"virtual","name":"BlackHole","rate":44100,"channels":2,"default":false,"loopback":false},
          {"id":"tropicast:system-audio","name":"System audio","rate":48000,"channels":2,"default":true,"loopback":true}
        ]
        """;

    [Fact]
    public void Physical_virtual_and_system_sources_use_stable_ids_and_separate_defaults()
    {
        var devices = CoreAudioBackend.ParseDevices(Devices);
        Assert.Equal(3, devices.Count);
        Assert.Equal(AudioDeviceKind.Input, devices[0].Kind);
        Assert.True(devices[0].IsDefault);
        Assert.Equal(new AudioFormat(48000, 1), devices[0].NativeFormat);
        Assert.Equal(AudioDeviceKind.Loopback, devices[1].Kind);
        Assert.Equal(AudioDeviceKind.Input, devices[2].Kind);
        Assert.Equal("virtual", devices[2].Id);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""[{"id":null}]""")]
    [InlineData("""[{"id":"a","name":"A","rate":48000,"channels":9,"default":true,"loopback":false}]""")]
    public void Malformed_data_is_an_error_not_empty_devices(string json)
        => Assert.Throws<IOException>(() => CoreAudioBackend.ParseDevices(json));

    [Fact]
    public void Duplicate_uids_are_rejected()
        => Assert.Throws<IOException>(() => CoreAudioBackend.ParseDevices(Devices.Replace("virtual", "built-in", StringComparison.Ordinal)));

    [Fact]
    public async Task Removal_and_default_changes_refresh_and_stop_without_fallback()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var devices = CoreAudioBackend.ParseDevices(Devices);
        var backend = new FakeBackend(devices);
        using var provider = new MacAudioCaptureProvider(backend);
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.StartAsync("built-in", cancellationToken: deadline.Token);
        Assert.True(service.Snapshot.IsCapturing);
        backend.Devices = devices.Select(d => d with { IsDefault = d.Id == "virtual" }).ToArray();
        await UntilAsync(() => service.Snapshot.Devices.Any(d => d.Id == "virtual" && d.IsDefault), deadline.Token);
        Assert.Equal("built-in", service.Snapshot.ActiveDeviceId);
        backend.Devices = backend.Devices.Where(d => d.Id != "built-in").ToArray();
        await UntilAsync(() => !service.Snapshot.IsCapturing, deadline.Token);
        Assert.Null(service.Snapshot.ActiveDeviceId);
        Assert.Contains("disconnected", service.Snapshot.Message, StringComparison.Ordinal);
        Assert.Equal(1, backend.Source.Disposals);
    }

    [Fact]
    public async Task Enumeration_errors_are_visible_and_watcher_recovers()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var backend = new FakeBackend(CoreAudioBackend.ParseDevices(Devices));
        using var provider = new MacAudioCaptureProvider(backend);
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.StartAsync("built-in", cancellationToken: deadline.Token);
        backend.Error = new IOException("audio service lost");
        await UntilAsync(() => !service.Snapshot.IsCapturing, deadline.Token);
        Assert.Contains("Cannot enumerate", service.Snapshot.Message, StringComparison.Ordinal);
        backend.Devices = [new("new", "New interface", AudioDeviceKind.Input, true, new(48000, 2))];
        backend.Error = null;
        await UntilAsync(() => service.Snapshot.Devices.Any(d => d.Id == "new"), deadline.Token);
        Assert.False(service.Snapshot.IsCapturing);
    }

    [Fact]
    public async Task Permission_denial_is_visible_and_source_released()
    {
        var backend = new FakeBackend(CoreAudioBackend.ParseDevices(Devices))
        {
            Source = new FakeSource { StartError = MacAudioErrors.Describe(1) },
        };
        using var provider = new MacAudioCaptureProvider(backend);
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.StartAsync("built-in", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("Microphone", service.Snapshot.Message, StringComparison.Ordinal);
        Assert.Equal(1, backend.Source.Disposals);
    }

    [Fact]
    public async Task Missing_source_is_not_opened_and_disposed_provider_rejects_access()
    {
        var backend = new FakeBackend([]);
        var provider = new MacAudioCaptureProvider(backend);
        await Assert.ThrowsAsync<IOException>(() => provider.StartAsync("missing", TestContext.Current.CancellationToken));
        provider.Dispose();
        provider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetDevicesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, backend.Opens);
    }

    [Fact]
    public void Native_macos_bridge_enumerates_without_prompting_or_recording()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Native Core Audio bridge smoke test runs on macOS CI.");
            return;
        }
        var devices = new CoreAudioBackend().GetDevices();
        var system = Assert.Single(devices, d => d.Id == "tropicast:system-audio");
        Assert.Equal(AudioDeviceKind.Loopback, system.Kind);
        Assert.Equal(new AudioFormat(48000, 2), system.NativeFormat);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(50, token);
        }
    }

    private sealed class FakeBackend(IReadOnlyList<AudioDevice> devices) : IMacAudioBackend
    {
        private IReadOnlyList<AudioDevice> _devices = devices;
        private IOException? _error;
        internal IReadOnlyList<AudioDevice> Devices { get => Volatile.Read(ref _devices); set => Volatile.Write(ref _devices, value); }
        internal IOException? Error { get => Volatile.Read(ref _error); set => Volatile.Write(ref _error, value); }
        internal FakeSource Source { get; init; } = new();
        internal int Opens { get; private set; }
        public IReadOnlyList<AudioDevice> GetDevices() => Error is { } error ? throw error : Devices;
        public IMacAudioSource Open(AudioDevice device)
        {
            Opens++;
            return Source;
        }
    }
}
