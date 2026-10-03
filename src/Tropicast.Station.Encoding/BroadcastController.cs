using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.Encoding;

public enum BroadcastState { Idle, Connecting, Live, Reconnecting, Stopping, Error }
public sealed record BroadcastSnapshot(BroadcastState State, string Message, TimeSpan Elapsed,
    int RetryAttempt = 0, TimeSpan RetryIn = default, bool IsRetryConnecting = false,
    int ReconnectCount = 0, TimeSpan Downtime = default)
{
    public bool IsActive => State is BroadcastState.Connecting or BroadcastState.Live or BroadcastState.Reconnecting or BroadcastState.Stopping;
}
public sealed class BroadcastChangedEventArgs(BroadcastSnapshot snapshot) : EventArgs
{
    public BroadcastSnapshot Snapshot { get; } = snapshot;
}

/// <summary>Owns capture and publishing, keeping capture alive during transient publisher failures.</summary>
public sealed partial class BroadcastController(
    AudioCaptureService capture, IBroadcastTargetProvider targets, IBroadcastEncoder encoder,
    ILogger<BroadcastController> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _sync = new();
    private readonly Stopwatch _liveTime = new();
    private readonly Stopwatch _downtime = new();
    private readonly Stopwatch _retryWait = new();
    private BroadcastSnapshot _snapshot = new(BroadcastState.Idle, "Select a saved profile and audio source.", TimeSpan.Zero);
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watcher;
    private IEncoderSession? _session;
    private BroadcastTarget? _target;
    private EncoderOptions? _options;
    private IOException? _frameFailure;
    private TimeSpan _retryDelay;
    private int _retryAttempt;
    private int _reconnectCount;
    private bool _retryConnecting;
    private bool _sessionIsRetry;
    private bool _hasBeenLive;
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
            _downtime.Reset();
            _retryWait.Reset();
            _retryAttempt = 0;
            _reconnectCount = 0;
            _hasBeenLive = false;
            _sessionIsRetry = false;
            _retryConnecting = false;
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
                _target = await targets.GetAsync(profileId, starting.Token).ConfigureAwait(false);
                _options = options ?? new();
                await capture.StartAsync(deviceId, _options.Format, starting.Token).ConfigureAwait(false);
                if (!capture.Snapshot.IsCapturing)
                {
                    throw new EncoderException(capture.Snapshot.Message);
                }
                capture.FrameAvailable += OnFrame;
                try
                {
                    Volatile.Write(ref _session, await encoder.StartAsync(_target, _options, starting.Token).ConfigureAwait(false));
                }
                catch (IOException ex) when (CanRetry(ex))
                {
                    ScheduleRetry(ex.Message);
                }
                starting.Token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    _watchCancellation = new();
                    var watchToken = _watchCancellation.Token;
                    _watcher = Task.Run(() => WatchAsync(watchToken), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (starting.IsCancellationRequested)
            {
                var error = await CleanupAsync().ConfigureAwait(false);
                Publish(error is null ? BroadcastState.Idle : BroadcastState.Error, error ?? "Connection cancelled.");
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
                    if (!capture.Snapshot.IsCapturing)
                    {
                        await EndWithErrorAsync(capture.Snapshot.Message).ConfigureAwait(false);
                        return;
                    }
                    if (_session is { } session)
                    {
                        IOException? failure = Volatile.Read(ref _frameFailure);
                        if (session.Completion.IsCompleted)
                        {
                            try
                            {
                                await session.Completion.ConfigureAwait(false);
                                failure ??= new IOException("The publisher ended unexpectedly.");
                            }
                            catch (IOException ex)
                            {
                                failure = ex;
                            }
                        }
                        if (failure is not null)
                        {
                            _liveTime.Stop();
                            if (_hasBeenLive)
                            {
                                _downtime.Start();
                            }
                            var releaseError = await ReleaseSessionAsync().ConfigureAwait(false);
                            failure = releaseError is not null && !CanRetry(releaseError) ? releaseError : failure;
                            if (!CanRetry(failure))
                            {
                                await EndWithErrorAsync(failure.Message).ConfigureAwait(false);
                                return;
                            }
                            ScheduleRetry(failure.Message);
                        }
                        else if (session.Snapshot.State == EncoderState.Streaming)
                        {
                            if (!_liveTime.IsRunning)
                            {
                                _liveTime.Start();
                                _downtime.Stop();
                                if (_sessionIsRetry)
                                {
                                    _reconnectCount++;
                                }
                                _hasBeenLive = true;
                                _sessionIsRetry = false;
                                _retryConnecting = false;
                                _retryAttempt = 0;
                                _retryWait.Reset();
                            }
                            Publish(BroadcastState.Live, "Live — publishing audio to Icecast.");
                        }
                    }
                    else if (Snapshot.State == BroadcastState.Reconnecting)
                    {
                        if (_retryWait.Elapsed < _retryDelay)
                        {
                            Publish(BroadcastState.Reconnecting, Snapshot.Message);
                            continue;
                        }
                        _retryConnecting = true;
                        Publish(BroadcastState.Reconnecting, $"Reconnect attempt {_retryAttempt}: connecting…");
                        try
                        {
                            var next = await encoder.StartAsync(_target!, _options, token).ConfigureAwait(false);
                            Interlocked.Exchange(ref _frameFailure, null);
                            Volatile.Write(ref _session, next);
                            _sessionIsRetry = true;
                            // Live is confirmed only after the new publisher sends actual MP3 audio.
                            Publish(BroadcastState.Reconnecting, $"Reconnect attempt {_retryAttempt}: waiting for audio…");
                        }
                        catch (IOException ex) when (CanRetry(ex))
                        {
                            ScheduleRetry(ex.Message);
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                        {
                            await EndWithErrorAsync(SafeMessage(ex)).ConfigureAwait(false);
                            return;
                        }
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

    internal static TimeSpan Backoff(int attempt, double jitter)
        => TimeSpan.FromSeconds(Math.Min(30, Math.Min(30, Math.Pow(2, Math.Min(Math.Max(attempt - 1, 0), 5))) * (0.8 + 0.4 * jitter)));

    internal static bool CanRetry(IOException error) => error switch
    {
        IcecastSourceException source => source.Status is ConnectionTestStatus.Unreachable or ConnectionTestStatus.TimedOut,
        EncoderException encoderError => encoderError.IsTransient,
        _ => true,
    };

    private void ScheduleRetry(string message)
    {
        _liveTime.Stop();
        if (_hasBeenLive)
        {
            _downtime.Start();
        }
        _retryAttempt++;
        _retryDelay = Backoff(_retryAttempt, Random.Shared.NextDouble());
        _retryWait.Restart();
        _retryConnecting = false;
        LogFailure(logger, "TransientConnectionFailure");
        Publish(BroadcastState.Reconnecting, $"{message} Automatic retry is scheduled; capture continues.");
    }

    public async Task RetryNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Snapshot.State != BroadcastState.Reconnecting || _session is not null)
            {
                return;
            }
            _retryDelay = TimeSpan.Zero;
            Publish(BroadcastState.Reconnecting, "Retry now requested; connecting…");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndWithErrorAsync(string message)
    {
        var cleanup = await CleanupAsync().ConfigureAwait(false);
        LogFailure(logger, "BroadcastInterrupted");
        Publish(BroadcastState.Error, cleanup ?? message);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _startCancellation?.Cancel();
            _watchCancellation?.Cancel();
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

    private async Task<IOException?> ReleaseSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return null;
        }
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (IOException ex)
        {
            LogFailure(logger, ex.GetType().Name);
            return ex;
        }
        finally
        {
            Interlocked.Exchange(ref _frameFailure, null);
        }
    }

    private async Task<string?> CleanupAsync()
    {
        capture.FrameAvailable -= OnFrame;
        lock (_sync)
        {
            _watchCancellation?.Cancel();
        }
        _liveTime.Stop();
        _downtime.Stop();
        _retryWait.Reset();
        _retryConnecting = false;
        await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        var error = await ReleaseSessionAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _watchCancellation?.Dispose();
            _watchCancellation = null;
        }
        _target = null;
        _options = null;
        return error?.Message;
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        IOException or SecretStoreException => ex.Message,
        InvalidOperationException => "The selected profile has no valid credentials or broadcasting is already active. Check the saved profile.",
        _ => "The broadcast settings are invalid. Select an MP3 profile and supported audio format.",
    };

    private void Publish(BroadcastState state, string message)
    {
        var retryIn = state == BroadcastState.Reconnecting && _session is null && !_retryConnecting
            ? _retryDelay - _retryWait.Elapsed : TimeSpan.Zero;
        var snapshot = new BroadcastSnapshot(state, message, _liveTime.Elapsed, _retryAttempt,
            retryIn > TimeSpan.Zero ? retryIn : TimeSpan.Zero, _retryConnecting, _reconnectCount, _downtime.Elapsed);
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
