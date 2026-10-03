using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Encoding;

public enum BroadcastState { Idle, Connecting, Live, Reconnecting, Stopping, Error }
public sealed record BroadcastSnapshot(BroadcastState State, string Message, TimeSpan Elapsed)
{
    public bool IsActive => State is BroadcastState.Connecting or BroadcastState.Live or BroadcastState.Reconnecting or BroadcastState.Stopping;
}
public sealed class BroadcastChangedEventArgs(BroadcastSnapshot snapshot) : EventArgs
{
    public BroadcastSnapshot Snapshot { get; } = snapshot;
}

/// <summary>Owns capture and publishing together; no retry until the reconnect policy in #11.</summary>
public sealed partial class BroadcastController(
    AudioCaptureService capture, IBroadcastTargetProvider targets, IBroadcastEncoder encoder,
    ILogger<BroadcastController> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _sync = new();
    private readonly Stopwatch _liveTime = new();
    private BroadcastSnapshot _snapshot = new(BroadcastState.Idle, "Select a saved profile and audio source.", TimeSpan.Zero);
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watcher;
    private IEncoderSession? _session;
    private IOException? _frameFailure;
    private bool _disposed;

    public BroadcastSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event EventHandler<BroadcastChangedEventArgs>? Changed;

    public async Task StartAsync(Guid profileId, string deviceId, EncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Snapshot.IsActive)
            {
                throw new InvalidOperationException("A broadcast is already active.");
            }
            _liveTime.Reset();
            _frameFailure = null;
            using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                _startCancellation = starting;
            }
            Publish(BroadcastState.Connecting, "Connecting to the saved profile…");
            try
            {
                await capture.StopAsync(starting.Token).ConfigureAwait(false);
                var target = await targets.GetAsync(profileId, starting.Token).ConfigureAwait(false);
                options ??= new();
                _session = await encoder.StartAsync(target, options, starting.Token).ConfigureAwait(false);
                capture.FrameAvailable += OnFrame;
                await capture.StartAsync(deviceId, options.Format, starting.Token).ConfigureAwait(false);
                if (!capture.Snapshot.IsCapturing)
                {
                    throw new IOException(capture.Snapshot.Message);
                }
                starting.Token.ThrowIfCancellationRequested();
                _watchCancellation = new();
                var watchToken = _watchCancellation.Token;
                _watcher = Task.Run(() => WatchAsync(watchToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (starting.IsCancellationRequested)
            {
                var error = await CleanupAsync().ConfigureAwait(false);
                Publish(error is null ? BroadcastState.Idle : BroadcastState.Error,
                    error ?? "Connection cancelled.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or SecretStoreException)
            {
                var cleanup = await CleanupAsync().ConfigureAwait(false);
                LogFailure(logger, ex.GetType().Name);
                Publish(BroadcastState.Error, cleanup ?? SafeMessage(ex));
            }
            finally
            {
                lock (_sync)
                {
                    _startCancellation = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnFrame(object? sender, PcmFrameEventArgs e)
    {
        try
        {
            Volatile.Read(ref _session)?.Submit(e.Frame);
        }
        catch (IOException ex)
        {
            Interlocked.CompareExchange(ref _frameFailure, ex, null);
        }
    }

    private async Task WatchAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var session = _session;
                    if (session is null)
                    {
                        return;
                    }
                    if (_frameFailure is not null || !capture.Snapshot.IsCapturing || session.Completion.IsCompleted)
                    {
                        var message = _frameFailure?.Message ?? (!capture.Snapshot.IsCapturing
                            ? capture.Snapshot.Message : session.Snapshot.Message);
                        var cleanup = await CleanupAsync().ConfigureAwait(false);
                        LogFailure(logger, "BroadcastInterrupted");
                        Publish(BroadcastState.Error, cleanup ?? message);
                        return;
                    }
                    if (session.Snapshot.State == EncoderState.Streaming)
                    {
                        if (!_liveTime.IsRunning)
                        {
                            _liveTime.Start();
                        }
                        Publish(BroadcastState.Live, "Live — publishing audio to Icecast.");
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _startCancellation?.Cancel();
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? watcher;
        try
        {
            if (_disposed)
            {
                return;
            }
            watcher = _watcher;
            _watchCancellation?.Cancel();
            Publish(BroadcastState.Stopping, "Stopping capture and finishing the stream…");
            var error = await CleanupAsync().ConfigureAwait(false);
            Publish(error is null ? BroadcastState.Idle : BroadcastState.Error, error ?? "Broadcast stopped.");
        }
        finally
        {
            _gate.Release();
        }
        if (watcher is not null)
        {
            await watcher.ConfigureAwait(false);
        }
    }

    private async Task<string?> CleanupAsync()
    {
        capture.FrameAvailable -= OnFrame;
        _watchCancellation?.Cancel();
        _liveTime.Stop();
        string? error = null;
        await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (_session is { } session)
        {
            _session = null;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                LogFailure(logger, ex.GetType().Name);
                error = session.Snapshot.Message;
            }
        }
        _watchCancellation?.Dispose();
        _watchCancellation = null;
        return error;
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        IOException or SecretStoreException => ex.Message,
        InvalidOperationException => "The selected profile has no valid credentials or broadcasting is already active. Check the saved profile.",
        _ => "The broadcast settings are invalid. Select an MP3 profile and supported audio format.",
    };

    private void Publish(BroadcastState state, string message)
    {
        var snapshot = new BroadcastSnapshot(state, message, _liveTime.Elapsed);
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke(this, new(snapshot));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broadcast failed ({ErrorType}); sensitive diagnostics omitted")]
    private static partial void LogFailure(ILogger logger, string errorType);
}
