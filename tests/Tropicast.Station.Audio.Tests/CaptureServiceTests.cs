using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace Tropicast.Station.Audio.Tests;

public sealed class CaptureServiceTests
{
    [Fact]
    public async Task Demo_frames_are_normalized_and_stop_unblocks_capture()
    {
        var provider = new ToneAudioCaptureProvider();
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, service.Snapshot.Devices.Count);
        var firstFrame = new TaskCompletionSource<PcmFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        service.FrameAvailable += (_, e) =>
        {
            Interlocked.Increment(ref count);
            firstFrame.TrySetResult(e.Frame);
        };
        await service.StartAsync("demo-input", cancellationToken: TestContext.Current.CancellationToken);
        var normalized = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(AudioFormat.EncoderDefault, normalized.Format);
        Assert.True(service.Snapshot.IsCapturing);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        var stoppedCount = count;
        await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.Equal(stoppedCount, count);
        await service.StartAsync("demo-input", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(service.Snapshot.IsCapturing);
    }

    [Fact]
    public async Task Active_device_removal_stops_without_switching_to_another_device()
    {
        var provider = new ToneAudioCaptureProvider();
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        await service.StartAsync("demo-input", cancellationToken: TestContext.Current.CancellationToken);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, e) =>
        {
            if (!e.Snapshot.IsCapturing && e.Snapshot.Message.Contains("disconnect", StringComparison.OrdinalIgnoreCase))
            {
                removed.TrySetResult();
            }
        };
        provider.SetDevices(service.Snapshot.Devices.Where(d => d.Kind == AudioDeviceKind.Loopback).ToArray());
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Null(service.Snapshot.ActiveDeviceId);
        Assert.Single(service.Snapshot.Devices);
        await service.StartAsync("demo-input", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("no longer available", service.Snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Idle_hotplug_and_default_changes_refresh_automatically()
    {
        var provider = new ToneAudioCaptureProvider();
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, e) =>
        {
            if (e.Snapshot.Devices.Count == 1)
            {
                updated.TrySetResult();
            }
        };
        provider.SetDevices([new("usb-input", "USB mixer", AudioDeviceKind.Input, true, new(48000, 2))]);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("usb-input", Assert.Single(service.Snapshot.Devices).Id);
    }

    [Fact]
    public async Task Format_change_stops_active_capture()
    {
        var provider = new ToneAudioCaptureProvider();
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        await service.StartAsync("demo-input", cancellationToken: TestContext.Current.CancellationToken);
        provider.SetDevices(service.Snapshot.Devices.Select(d => d with { NativeFormat = new(48000, 1) }).ToArray());
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("format changed", service.Snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_provider_never_pretends_to_capture_hardware()
    {
        await using var service = new AudioCaptureService(new UnavailableAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(service.Snapshot.Devices);
        Assert.Contains("--demo-audio", service.ProviderDescription, StringComparison.Ordinal);
        await service.StartAsync("not-real", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("no longer available", service.Snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_end_releases_session_and_displays_an_error()
    {
        var provider = new FailingProvider();
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        await service.StartAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        await provider.Session.Released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("ended unexpectedly", service.Snapshot.Message, StringComparison.Ordinal);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cleanup_error_is_not_overwritten_with_a_successful_stop_message()
    {
        var provider = new FailingProvider { Session = new EmptySession { WaitForStop = true, FailDispose = true } };
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.StartAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("could not be released", service.Snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enumeration_permission_error_is_visible_not_an_empty_success()
    {
        await using var service = new AudioCaptureService(new FailingProvider { FailEnumeration = true }, NullLogger<AudioCaptureService>.Instance);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("permissions", service.Snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposal_stops_active_capture_and_late_device_changes_are_safe()
    {
        var provider = new ToneAudioCaptureProvider();
        var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.StartAsync("demo-input", cancellationToken: TestContext.Current.CancellationToken);
        await service.DisposeAsync();
        await service.DisposeAsync();
        var devices = await provider.GetDevicesAsync(TestContext.Current.CancellationToken);
        provider.SetDevices(devices.Where(d => d.Kind == AudioDeviceKind.Loopback).ToArray());
        await using var restarted = await provider.StartAsync("demo-loopback", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tone_session_cancel_stop_and_single_consumer_contract()
    {
        var provider = new ToneAudioCaptureProvider();
        await using var session = await provider.StartAsync("demo-loopback", TestContext.Current.CancellationToken);
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new AudioFormat(48000, 2), reader.Current.Format);
        Assert.Equal(960, reader.Current.FrameCount);
        await using var second = session.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        await session.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(await reader.MoveNextAsync());
    }

    private sealed class FailingProvider : IAudioCaptureProvider
    {
        public EmptySession Session { get; init; } = new();
        public bool FailEnumeration { get; init; }
        public string Description => "Test adapter";
        public event EventHandler? DevicesChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => FailEnumeration ? throw new UnauthorizedAccessException("Microphone permission denied.")
                : Task.FromResult<IReadOnlyList<AudioDevice>>([new("test", "Test input", AudioDeviceKind.Input, true, new(48000, 2))]);
        public Task<IAudioCaptureSession> StartAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IAudioCaptureSession>(Session);
    }

    private sealed class EmptySession : IAudioCaptureSession
    {
        public bool WaitForStop { get; init; }
        public bool FailDispose { get; init; }
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (WaitForStop)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }
            yield break;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Released.TrySetResult();
            if (FailDispose)
            {
                throw new IOException("Simulated cleanup failure.");
            }
            return ValueTask.CompletedTask;
        }
    }
}
