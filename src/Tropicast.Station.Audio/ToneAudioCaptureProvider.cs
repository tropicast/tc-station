using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Tropicast.Station.Audio;

/// <summary>Explicit demo/test adapter. Generates PCM only; never records or plays system audio.</summary>
public sealed class ToneAudioCaptureProvider : IAudioCaptureProvider
{
    private readonly object _sync = new();
    private IReadOnlyList<AudioDevice> _devices =
    [
        new("demo-input", "Demo microphone — 440 Hz tone", AudioDeviceKind.Input, true, new(44100, 1, PcmEncoding.Signed16)),
        new("demo-loopback", "Demo application output — 660 Hz tone", AudioDeviceKind.Loopback, true, new(48000, 2)),
    ];
    private readonly Dictionary<string, ToneSession> _sessions = [];

    public string Description => "DEMO: synthetic tones only, not real microphones or application audio.";
    public event EventHandler? DevicesChanged;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_devices);
        }
    }

    public Task<IAudioCaptureSession> StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var device = _devices.FirstOrDefault(d => d.Id == deviceId)
                ?? throw new IOException("The selected demo device is no longer available.");
            if (_sessions.TryGetValue(deviceId, out var existing) && !existing.IsStopped)
            {
                throw new InvalidOperationException("This device already has an active capture session.");
            }

            var session = new ToneSession(device, device.Kind == AudioDeviceKind.Input ? 440 : 660);
            _sessions[deviceId] = session;
            return Task.FromResult<IAudioCaptureSession>(session);
        }
    }

    /// <summary>Simulates hot-plug for demos and tests, including stopping a removed source.</summary>
    public void SetDevices(IReadOnlyList<AudioDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != devices.Count)
        {
            throw new ArgumentException("Device IDs must be unique.", nameof(devices));
        }

        lock (_sync)
        {
            _devices = devices.ToArray();
            foreach (var (id, session) in _sessions)
            {
                if (!_devices.Any(d => d.Id == id))
                {
                    session.Remove();
                }
            }
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ToneSession(AudioDevice device, double frequency) : IAudioCaptureSession
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _removed;
        private bool _disposed;
        private int _reader;
        private long _sample;
        public bool IsStopped
        {
            get
            {
                lock (_sync)
                {
                    return _disposed || _stop.IsCancellationRequested;
                }
            }
        }

        public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _reader, 1) != 0)
            {
                throw new InvalidOperationException("A capture session supports only one reader.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (!linked.IsCancellationRequested)
            {
                bool tick;
                try
                {
                    tick = await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (_removed)
                    {
                        throw new IOException("The selected audio device was disconnected.");
                    }

                    yield break;
                }

                if (!tick)
                {
                    yield break;
                }

                yield return NextFrame();
            }

            if (_removed)
            {
                throw new IOException("The selected audio device was disconnected.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        private PcmFrame NextFrame()
        {
            var format = device.NativeFormat;
            var frames = format.SampleRate / 50;
            var bytes = new byte[frames * format.BytesPerFrame];
            var width = format.BytesPerFrame / format.Channels;
            for (var i = 0; i < frames; i++)
            {
                var value = (float)(0.2 * Math.Sin(2 * Math.PI * frequency * _sample++ / format.SampleRate));
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var target = bytes.AsSpan((i * format.Channels + channel) * width);
                    if (format.Encoding == PcmEncoding.Signed16)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(target, (short)Math.Round(value * 32767));
                    }
                    else
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(value));
                    }
                }
            }

            return new PcmFrame(format, bytes);
        }

        public void Remove()
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _removed = true;
                    _stop.Cancel();
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _stop.Cancel();
                }
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _stop.Cancel();
                    _stop.Dispose();
                    _disposed = true;
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
