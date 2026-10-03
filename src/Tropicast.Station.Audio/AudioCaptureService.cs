using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Tropicast.Station.Audio;

public sealed record AudioCaptureSnapshot(IReadOnlyList<AudioDevice> Devices, string? ActiveDeviceId, bool IsCapturing, string Message);
public sealed class AudioSnapshotEventArgs(AudioCaptureSnapshot snapshot) : EventArgs
{
    public AudioCaptureSnapshot Snapshot { get; } = snapshot;
}
public sealed class PcmFrameEventArgs(PcmFrame frame) : EventArgs
{
    public PcmFrame Frame { get; } = frame;
}

/// <summary>
/// Serializes device changes and capture lifecycle. Subscribers run on the capture/background
/// thread and must not block or throw. Frames have already been normalized for the encoder.
/// </summary>
public sealed partial class AudioCaptureService : IAsyncDisposable, IDisposable
{
    private readonly IAudioCaptureProvider _provider;
    private readonly ILogger<AudioCaptureService> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
    });
    private readonly Task _watcher;
    private IAudioCaptureSession? _session;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private AudioCaptureSnapshot _snapshot = new([], null, false, "Select an audio source.");
    private bool _disposed;
    private long _errorVersion;

    public AudioCaptureService(IAudioCaptureProvider provider, ILogger<AudioCaptureService> logger)
    {
        _provider = provider;
        _logger = logger;
        provider.DevicesChanged += OnDevicesChanged;
        _watcher = WatchDevicesAsync();
    }

    public AudioCaptureSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public string ProviderDescription => _provider.Description;
    public event EventHandler<AudioSnapshotEventArgs>? Changed;
    public event EventHandler<PcmFrameEventArgs>? FrameAvailable;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var devices = await _provider.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (devices.Any(d => string.IsNullOrWhiteSpace(d.Id))
                || devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != devices.Count)
            {
                throw new IOException("The audio provider returned invalid or duplicate device IDs.");
            }

            var current = Snapshot;
            var active = devices.FirstOrDefault(d => d.Id == current.ActiveDeviceId);
            var previous = current.Devices.FirstOrDefault(d => d.Id == current.ActiveDeviceId);
            if (current.ActiveDeviceId is not null && (active is null || active.NativeFormat != previous?.NativeFormat))
            {
                if (await StopCoreAsync().ConfigureAwait(false))
                {
                    Publish(new(devices.ToArray(), null, false, active is null
                        ? "The selected audio device was disconnected. Capture stopped; select another source."
                        : "The selected device format changed. Capture stopped; select the source again."));
                }
                else
                {
                    Update(s => s with { Devices = devices.ToArray() });
                }
            }
            else
            {
                Update(s => s with { Devices = devices.ToArray() });
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException
            && ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            if (await StopCoreAsync().ConfigureAwait(false))
            {
                ReportError("Cannot enumerate audio devices. Check the audio service and permissions.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(string deviceId, AudioFormat? targetFormat = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!await StopCoreAsync().ConfigureAwait(false))
            {
                throw new IOException("The previous audio source did not stop cleanly. Restart the audio service before retrying.");
            }
            var devices = await _provider.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            var selected = devices.FirstOrDefault(d => d.Id == deviceId)
                ?? throw new IOException("The selected audio device is no longer available.");
            var converter = new PcmConverter(selected.NativeFormat, targetFormat ?? AudioFormat.EncoderDefault);
            _session = await _provider.StartAsync(deviceId, cancellationToken).ConfigureAwait(false);
            _captureCancellation = new CancellationTokenSource();
            Publish(new(devices.ToArray(), deviceId, true, $"Capturing {selected.DisplayName} (preview only; not broadcasting)."));
            _captureTask = ReadAsync(_session, converter, _captureCancellation.Token);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException
            && ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            await StopCoreAsync().ConfigureAwait(false);
            ReportError(ex is IOException ? ex.Message : "Could not start capture. Check the device and its audio format.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await StopCoreAsync().ConfigureAwait(false))
            {
                Update(s => s with { ActiveDeviceId = null, IsCapturing = false, Message = "Capture stopped." });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReadAsync(IAudioCaptureSession session, PcmConverter converter, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in session.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (converter.Convert(frame) is { } normalized)
                {
                    FrameAvailable?.Invoke(this, new(normalized));
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Audio capture ended unexpectedly. The selected device may have disconnected.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ReportError(ex.Message, ex);
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                ReportError("The audio device could not be released cleanly. Restart the audio service before retrying.", ex);
            }
        }
    }

    private async Task<bool> StopCoreAsync()
    {
        if (_session is null)
        {
            return true;
        }

        var errorVersion = Interlocked.Read(ref _errorVersion);
        _captureCancellation!.Cancel();
        // The reader owns disposal; do not call StopAsync on a session that has already ended.
        if (_captureTask is { IsCompleted: false })
        {
            try
            {
                await _session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                ReportError("The audio provider reported an error while stopping capture.", ex);
            }
        }

        if (_captureTask is not null)
        {
            await _captureTask.ConfigureAwait(false);
        }

        _captureCancellation.Dispose();
        _captureCancellation = null;
        _session = null;
        _captureTask = null;
        return errorVersion == Interlocked.Read(ref _errorVersion);
    }

    private void ReportError(string message, Exception exception)
    {
        LogCaptureError(_logger, exception.GetType().Name);
        Interlocked.Increment(ref _errorVersion);
        Update(s => s with { ActiveDeviceId = null, IsCapturing = false, Message = message });
    }

    private void Publish(AudioCaptureSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke(this, new(snapshot));
    }

    private void Update(Func<AudioCaptureSnapshot, AudioCaptureSnapshot> update)
    {
        AudioCaptureSnapshot previous, next;
        do
        {
            previous = Snapshot;
            next = update(previous);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, previous), previous));

        Changed?.Invoke(this, new(next));
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _changes.Writer.TryWrite(true);

    private async Task WatchDevicesAsync()
    {
        await foreach (var _ in _changes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _provider.DevicesChanged -= OnDevicesChanged;
        _changes.Writer.TryComplete();
        await _watcher.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                await StopCoreAsync().ConfigureAwait(false);
                _disposed = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio capture failed ({ErrorType})")]
    private static partial void LogCaptureError(ILogger logger, string errorType);
}
