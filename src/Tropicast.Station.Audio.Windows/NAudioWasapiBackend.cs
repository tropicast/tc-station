using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Tropicast.Station.Audio.Windows;

[SupportedOSPlatform("windows")]
internal sealed class NAudioWasapiBackend : IWasapiBackend
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly WindowsEndpointNotifications _notifications;
    private volatile bool _disposed;

    internal NAudioWasapiBackend()
    {
        _notifications = new WindowsEndpointNotifications(Notify);
        var result = _enumerator.RegisterEndpointNotificationCallback(_notifications);
        if (result != 0)
        {
            _enumerator.Dispose();
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var devices = new List<AudioDevice>();
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
        {
            string? defaultId = null;
            if (_enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                using var defaultDevice = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = defaultDevice.ID;
            }

            foreach (var endpoint in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (endpoint)
                using (var client = endpoint.AudioClient)
                {
                    var format = WasapiFormats.Negotiate(client.MixFormat);
                    devices.Add(new(endpoint.ID, endpoint.FriendlyName,
                        flow == DataFlow.Capture ? AudioDeviceKind.Input : AudioDeviceKind.Loopback,
                        endpoint.ID == defaultId, format));
                }
            }
        }

        return devices;
    }

    public bool IsMicrophoneDenied()
    {
        const string consent = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        // Registry is only a preflight hint; WASAPI's actual access-denied HRESULT remains authoritative.
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var microphone = root.OpenSubKey(consent);
            using var desktop = root.OpenSubKey(consent + @"\NonPackaged");
            if (string.Equals(microphone?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)
                || string.Equals(desktop?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public IWasapiSource Open(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var device = _enumerator.GetDevice(deviceId);
        WasapiCapture? capture = null;
        try
        {
            if (device.State != DeviceState.Active)
            {
                throw new IOException("The selected Windows audio endpoint is no longer active.");
            }

            capture = device.DataFlow == DataFlow.Capture
                ? new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 100)
                : new WasapiLoopbackCapture(device);
            var format = WasapiFormats.Negotiate(capture.WaveFormat);
            capture.ShareMode = AudioClientShareMode.Shared;
            capture.WaveFormat = format.Encoding == PcmEncoding.Signed16
                ? new WaveFormat(format.SampleRate, 16, format.Channels)
                : WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            return new NAudioSource(device, capture, format);
        }
        catch
        {
            capture?.Dispose();
            device.Dispose();
            throw;
        }
    }

    private void Notify()
    {
        if (!_disposed)
        {
            // The subscriber only schedules refresh; never enumerate, stop or unregister inside a COM callback.
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            var result = _enumerator.UnregisterEndpointNotificationCallback(_notifications);
            _enumerator.Dispose();
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private sealed class NAudioSource : IWasapiSource
    {
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private bool _disposed;

        internal NAudioSource(MMDevice device, WasapiCapture capture, AudioFormat format)
        {
            _device = device;
            _capture = capture;
            Format = format;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
        }

        public AudioFormat Format { get; }
        public event EventHandler<WasapiDataEventArgs>? DataAvailable;
        public event EventHandler<WasapiStoppedEventArgs>? Stopped;
        public void Start() => _capture.StartRecording();
        public void Stop() => _capture.StopRecording();
        private void OnData(object? sender, WaveInEventArgs e) => DataAvailable?.Invoke(this, new(e.Buffer, e.BytesRecorded));
        private void OnStopped(object? sender, StoppedEventArgs e) => Stopped?.Invoke(this, new(e.Exception));

        public void Dispose()
        {
            if (!_disposed)
            {
                _capture.DataAvailable -= OnData;
                _capture.RecordingStopped -= OnStopped;
                try
                {
                    _capture.Dispose();
                }
                finally
                {
                    _device.Dispose();
                    _disposed = true;
                }
            }
        }
    }
}

internal static class WasapiFormats
{
    /// <summary>
    /// Preserve float32/PCM16 mix formats. Shared WASAPI auto-converts PCM24/32 to float32;
    /// native sample rate/channel count remain unchanged. Resampling/downmix is the shared pipeline's job.
    /// </summary>
    internal static AudioFormat Negotiate(WaveFormat mix)
    {
        ArgumentNullException.ThrowIfNull(mix);
        if (mix is WaveFormatExtensible extensible)
        {
            mix = extensible.ToStandardWaveFormat();
        }

        if (mix.Encoding == WaveFormatEncoding.Pcm && mix.BitsPerSample is 16 or 24 or 32)
        {
            return new(mix.SampleRate, mix.Channels, mix.BitsPerSample == 16 ? PcmEncoding.Signed16 : PcmEncoding.Float32);
        }

        if (mix.Encoding == WaveFormatEncoding.IeeeFloat && mix.BitsPerSample == 32)
        {
            return new(mix.SampleRate, mix.Channels);
        }

        throw new IOException("The Windows audio endpoint has an unsupported mix format. Configure PCM16/24/32 or float32 in its Sound properties.");
    }
}
