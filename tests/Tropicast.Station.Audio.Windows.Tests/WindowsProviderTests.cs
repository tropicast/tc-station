using NAudio.Wave;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
#pragma warning disable CA2201 // Deliberately simulate native WASAPI HRESULT failures.

namespace Tropicast.Station.Audio.Windows.Tests;

public sealed class WindowsProviderTests
{
    [Fact]
    public void Every_endpoint_notification_requests_refresh()
    {
        var count = 0;
        var notifications = new WindowsEndpointNotifications(() => count++);
        notifications.OnDeviceAdded("usb");
        notifications.OnDeviceRemoved("usb");
        notifications.OnDeviceStateChanged("usb", DeviceState.Disabled);
        notifications.OnDefaultDeviceChanged(DataFlow.Capture, Role.Multimedia, "usb");
        notifications.OnPropertyValueChanged("usb", default);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task Enumeration_start_and_notifications_use_selected_endpoint_on_worker_thread()
    {
        var backend = new FakeBackend();
        using var provider = new WindowsAudioCaptureProvider(() => backend);
        var notifications = 0;
        provider.DevicesChanged += (_, _) => notifications++;
        var devices = await provider.GetDevicesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AudioDeviceKind.Input, devices[0].Kind);
        Assert.Equal(AudioDeviceKind.Loopback, devices[1].Kind);
        await using var session = await provider.StartAsync("speakers", TestContext.Current.CancellationToken);
        Assert.False(backend.OpenedWithSynchronizationContext);
        Assert.Equal(1, backend.Source.Starts);
        Assert.Equal(new AudioFormat(44100, 2, PcmEncoding.Signed16), backend.Source.Format);
        backend.Notify();
        Assert.Equal(1, notifications);
        provider.Dispose();
        backend.Notify();
        Assert.Equal(1, notifications);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task Microphone_denial_has_guidance_but_does_not_block_loopback()
    {
        var backend = new FakeBackend { Denied = true };
        using var provider = new WindowsAudioCaptureProvider(() => backend);
        var error = await Assert.ThrowsAsync<IOException>(() => provider.StartAsync("usb-mixer", TestContext.Current.CancellationToken));
        Assert.Contains("Let desktop apps access your microphone", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, backend.Source.Starts);
        await using var session = await provider.StartAsync("speakers", TestContext.Current.CancellationToken);
        Assert.Equal(1, backend.Source.Starts);
    }

    [Fact]
    public async Task Removed_endpoint_is_not_replaced_by_default_and_shared_service_stops()
    {
        var backend = new FakeBackend();
        using var provider = new WindowsAudioCaptureProvider(() => backend);
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        await service.StartAsync("usb-mixer", cancellationToken: TestContext.Current.CancellationToken);
        backend.Devices = backend.Devices.Where(d => d.Kind == AudioDeviceKind.Loopback).ToArray();
        backend.Notify();
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsCapturing);
        Assert.Contains("disconnected", service.Snapshot.Message, StringComparison.Ordinal);
        Assert.Equal(1, backend.Source.Stops);
        Assert.Equal(1, backend.Source.Disposes);
        await Assert.ThrowsAsync<IOException>(() => provider.StartAsync("usb-mixer", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(16, PcmEncoding.Signed16)]
    [InlineData(24, PcmEncoding.Float32)]
    [InlineData(32, PcmEncoding.Float32)]
    public void Pcm_mix_formats_are_negotiated_without_changing_rate_or_channels(int bits, PcmEncoding encoding)
    {
        Assert.Equal(new AudioFormat(44100, 2, encoding), WasapiFormats.Negotiate(new WaveFormat(44100, bits, 2)));
        Assert.Equal(new AudioFormat(48000, 2, encoding), WasapiFormats.Negotiate(new WaveFormatExtensible(48000, bits, 2)));
    }

    [Fact]
    public void Float_extensible_is_supported_and_compressed_formats_are_rejected()
    {
        Assert.Equal(new AudioFormat(48000, 2), WasapiFormats.Negotiate(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        Assert.Throws<IOException>(() => WasapiFormats.Negotiate(WaveFormat.CreateALawFormat(8000, 1)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), "Microphone")]
    [InlineData(unchecked((int)0x88890004), "disconnected")]
    [InlineData(unchecked((int)0x8889000A), "service")]
    [InlineData(unchecked((int)0x88890008), "format")]
    [InlineData(unchecked((int)0x8889000E), "exclusive")]
    public void Native_errors_do_not_leak_diagnostics(int code, string expected)
    {
        var error = WindowsAudioErrors.Describe(new System.Runtime.InteropServices.COMException("Raw native diagnostic", code));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Raw native diagnostic", error.Message, StringComparison.Ordinal);
    }
}
