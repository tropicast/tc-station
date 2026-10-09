using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.Encoding;

public enum BroadcastState { Idle, Connecting, Live, Reconnecting, Stopping, Error }
public enum BroadcastOutputState { Connecting, Live, Reconnecting, Failed }
/// <summary>Status of one published stream (for example the MP3 or the Opus mount).</summary>
public sealed record BroadcastOutputSnapshot(string Codec, string Mount, BroadcastOutputState State, string Message);
public sealed record BroadcastSnapshot(BroadcastState State, string Message, TimeSpan Elapsed,
    int RetryAttempt = 0, TimeSpan RetryIn = default, bool IsRetryConnecting = false,
    int ReconnectCount = 0, TimeSpan Downtime = default, IReadOnlyList<BroadcastOutputSnapshot>? Outputs = null)
{
    public bool IsActive => State is BroadcastState.Connecting or BroadcastState.Live or BroadcastState.Reconnecting or BroadcastState.Stopping;
}
public sealed class BroadcastChangedEventArgs(BroadcastSnapshot snapshot) : EventArgs
{
    public BroadcastSnapshot Snapshot { get; } = snapshot;
}

/// <summary>
/// Owns capture and publishing, keeping capture alive during transient publisher failures. Each output of the
/// profile (MP3, and Opus when enabled) has its own publisher and reconnects on its own; losing one output does
/// not stop the others. The broadcast ends with an error only when every output has failed permanently.
/// </summary>
public sealed partial class BroadcastController(
    AudioCaptureService capture, IBroadcastTargetProvider targets, IBroadcastEncoder encoder,
    ILogger<BroadcastController> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _sync = new();
    private readonly Stopwatch _liveTime = new();
    private readonly Stopwatch _downtime = new();
    private BroadcastSnapshot _snapshot = new(BroadcastState.Idle, "Select a saved profile and audio source.", TimeSpan.Zero);
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watcher;
    private Output[] _outputs = [];
    private int _reconnectCount;
    private bool _hasBeenLive;
    private bool _disposed;

    public BroadcastSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event EventHandler<BroadcastChangedEventArgs>? Changed;

    /// <summary>One published stream and its retry state. Session and frame failure are shared with the capture thread.</summary>
    private sealed class Output(BroadcastTarget target, EncoderOptions options)
    {
        internal BroadcastTarget Target { get; } = target;
        internal EncoderOptions Options { get; } = options;
        internal Stopwatch RetryWait { get; } = new();
        internal IEncoderSession? Session;
        internal IOException? FrameFailure;
        internal Exception? Error;
        internal TimeSpan RetryDelay;
        internal int RetryAttempt;
        internal bool RetryConnecting;
        internal bool SessionIsRetry;
        internal BroadcastOutputState State = BroadcastOutputState.Connecting;
        internal string Message = "Connecting…";
    }

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
            _reconnectCount = 0;
            _hasBeenLive = false;
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
                var primary = options ?? EncoderOptions.FromProfile(target.Profile);
                var outputs = target.Profile.Outputs()
                    .Select((profile, index) => index == 0 ? new Output(target, primary)
                        : new Output(new BroadcastTarget(profile, target.Password), primary.ForOutput(profile)))
                    .ToArray();
                await capture.StartAsync(deviceId, primary.Format, starting.Token).ConfigureAwait(false);
                if (!capture.Snapshot.IsCapturing)
                {
                    throw new EncoderException(capture.Snapshot.Message);
                }
                Volatile.Write(ref _outputs, outputs);
                capture.FrameAvailable += OnFrame;
                foreach (var output in outputs)
                {
                    try
                    {
                        Volatile.Write(ref output.Session, await encoder.StartAsync(output.Target, output.Options, starting.Token).ConfigureAwait(false));
                    }
                    catch (IOException ex) when (CanRetry(ex))
                    {
                        ScheduleRetry(output, ex.Message);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                    {
                        FailOutput(output, ex);
                    }
                }
                starting.Token.ThrowIfCancellationRequested();
                if (outputs.All(o => o.State == BroadcastOutputState.Failed))
                {
                    ExceptionDispatchInfo.Throw(outputs[0].Error!);
                }
                PublishOutputs();
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
        foreach (var output in Volatile.Read(ref _outputs))
        {
            try
            {
                Volatile.Read(ref output.Session)?.Submit(e.Frame);
            }
            catch (IOException ex)
            {
                Interlocked.CompareExchange(ref output.FrameFailure, ex, null);
            }
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
                    var outputs = Volatile.Read(ref _outputs);
                    foreach (var output in outputs)
                    {
                        await CheckOutputAsync(output, token).ConfigureAwait(false);
                    }
                    if (outputs.Length > 0 && outputs.All(o => o.State == BroadcastOutputState.Failed))
                    {
                        await EndWithErrorAsync(outputs[0].Message).ConfigureAwait(false);
                        return;
                    }
                    PublishOutputs();
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CheckOutputAsync(Output output, CancellationToken token)
    {
        if (output.Session is { } session)
        {
            IOException? failure = Volatile.Read(ref output.FrameFailure);
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
                var releaseError = await ReleaseSessionAsync(output).ConfigureAwait(false);
                failure = releaseError is not null && !CanRetry(releaseError) ? releaseError : failure;
                if (CanRetry(failure))
                {
                    ScheduleRetry(output, failure.Message);
                }
                else
                {
                    FailOutput(output, failure);
                }
            }
            else if (session.Snapshot.State == EncoderState.Streaming && output.State != BroadcastOutputState.Live)
            {
                if (output.SessionIsRetry)
                {
                    _reconnectCount++;
                }
                output.State = BroadcastOutputState.Live;
                output.Message = $"Live — publishing {output.Options.CodecName}.";
                output.SessionIsRetry = false;
                output.RetryConnecting = false;
                output.RetryAttempt = 0;
                output.RetryWait.Reset();
                UpdateClocks();
            }
        }
        else if (output.State == BroadcastOutputState.Reconnecting && output.RetryWait.Elapsed >= output.RetryDelay)
        {
            output.RetryConnecting = true;
            output.Message = $"Reconnect attempt {output.RetryAttempt}: connecting…";
            PublishOutputs();
            try
            {
                var next = await encoder.StartAsync(output.Target, output.Options, token).ConfigureAwait(false);
                Interlocked.Exchange(ref output.FrameFailure, null);
                Volatile.Write(ref output.Session, next);
                output.SessionIsRetry = true;
                // Live is confirmed only after the new publisher sends actual audio.
                output.Message = $"Reconnect attempt {output.RetryAttempt}: waiting for audio…";
            }
            catch (IOException ex) when (CanRetry(ex))
            {
                ScheduleRetry(output, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                FailOutput(output, ex);
            }
        }
    }

    internal static TimeSpan Backoff(int attempt, double jitter)
        => TimeSpan.FromSeconds(Math.Min(30, Math.Min(30, Math.Pow(2, Math.Min(Math.Max(attempt - 1, 0), 5))) * (0.8 + 0.4 * jitter)));

    internal static bool CanRetry(IOException error) => error switch
    {
        TropicastSourceException source => source.Status is ConnectionTestStatus.Unreachable or ConnectionTestStatus.TimedOut,
        EncoderException encoderError => encoderError.IsTransient,
        _ => true,
    };

    private void ScheduleRetry(Output output, string message)
    {
        output.RetryAttempt++;
        output.RetryDelay = Backoff(output.RetryAttempt, Random.Shared.NextDouble());
        output.RetryWait.Restart();
        output.RetryConnecting = false;
        output.State = BroadcastOutputState.Reconnecting;
        output.Message = $"{message} Automatic retry is scheduled; capture continues.";
        LogFailure(logger, "TransientConnectionFailure");
        UpdateClocks();
        PublishOutputs();
    }

    private void FailOutput(Output output, Exception error)
    {
        output.Error = error;
        output.State = BroadcastOutputState.Failed;
        output.Message = SafeMessage(error);
        output.RetryConnecting = false;
        output.RetryWait.Reset();
        LogFailure(logger, error.GetType().Name);
        UpdateClocks();
    }

    /// <summary>Live time runs while any output is live; downtime runs while none is, after the first live.</summary>
    private void UpdateClocks()
    {
        if (Volatile.Read(ref _outputs).Any(o => o.State == BroadcastOutputState.Live))
        {
            _liveTime.Start();
            _downtime.Stop();
            _hasBeenLive = true;
        }
        else
        {
            _liveTime.Stop();
            if (_hasBeenLive)
            {
                _downtime.Start();
            }
        }
    }

    public async Task RetryNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var waiting = Volatile.Read(ref _outputs)
                .Where(o => o.State == BroadcastOutputState.Reconnecting && o.Session is null && !o.RetryConnecting).ToArray();
            if (waiting.Length == 0)
            {
                return;
            }
            foreach (var output in waiting)
            {
                output.RetryDelay = TimeSpan.Zero;
                output.Message = "Retry now requested; connecting…";
            }
            PublishOutputs();
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

    private async Task<IOException?> ReleaseSessionAsync(Output output)
    {
        var session = Interlocked.Exchange(ref output.Session, null);
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
            Interlocked.Exchange(ref output.FrameFailure, null);
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
        await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        IOException? error = null;
        foreach (var output in Volatile.Read(ref _outputs))
        {
            output.RetryWait.Reset();
            output.RetryConnecting = false;
            error ??= await ReleaseSessionAsync(output).ConfigureAwait(false);
        }
        lock (_sync)
        {
            _watchCancellation?.Dispose();
            _watchCancellation = null;
        }
        Volatile.Write(ref _outputs, []);
        return error?.Message;
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        IOException or SecretStoreException => ex.Message,
        InvalidOperationException => "The selected profile has no valid credentials or broadcasting is already active. Check the saved profile.",
        _ => "The broadcast settings are invalid. Select an MP3 or Opus profile and supported audio format.",
    };

    /// <summary>Publishes the overall state from the outputs: live if any output is live.</summary>
    private void PublishOutputs()
    {
        var outputs = Volatile.Read(ref _outputs);
        var live = outputs.Where(o => o.State == BroadcastOutputState.Live).ToArray();
        if (live.Length == outputs.Length && live.Length > 0)
        {
            Publish(BroadcastState.Live, "Live — publishing audio to Tropicast.");
        }
        else if (live.Length > 0)
        {
            var others = outputs.Where(o => o.State != BroadcastOutputState.Live)
                .Select(o => $"{o.Options.CodecName}: {o.Message}");
            Publish(BroadcastState.Live, $"Live — publishing {string.Join(" and ", live.Select(o => o.Options.CodecName))}. {string.Join(" ", others)}");
        }
        else if (outputs.FirstOrDefault(o => o.State == BroadcastOutputState.Reconnecting) is { } reconnecting)
        {
            Publish(BroadcastState.Reconnecting, reconnecting.Message);
        }
        else if (Snapshot.State != BroadcastState.Connecting)
        {
            Publish(BroadcastState.Connecting, "Connecting to the saved profile…");
        }
    }

    private void Publish(BroadcastState state, string message)
    {
        var outputs = Volatile.Read(ref _outputs);
        // Retry details describe the first output that is reconnecting.
        var focus = state == BroadcastState.Reconnecting
            ? outputs.FirstOrDefault(o => o.State == BroadcastOutputState.Reconnecting) : null;
        var retryIn = focus is { Session: null, RetryConnecting: false } ? focus.RetryDelay - focus.RetryWait.Elapsed : TimeSpan.Zero;
        var snapshot = new BroadcastSnapshot(state, message, _liveTime.Elapsed, focus?.RetryAttempt ?? 0,
            retryIn > TimeSpan.Zero ? retryIn : TimeSpan.Zero, focus?.RetryConnecting ?? false, _reconnectCount, _downtime.Elapsed,
            [.. outputs.Select(o => new BroadcastOutputSnapshot(o.Options.CodecName, o.Target.Profile.Mount, o.State, o.Message))]);
        Volatile.Write(ref _snapshot, snapshot);
        if (snapshot.State != _lastLoggedState)
        {
            LogState(logger, (int)snapshot.State, snapshot.ReconnectCount, snapshot.RetryAttempt,
                snapshot.Downtime.TotalSeconds, snapshot.Elapsed.TotalSeconds);
            _lastLoggedState = snapshot.State;
        }
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

    private BroadcastState _lastLoggedState = BroadcastState.Idle;

    [LoggerMessage(EventId = 1302, Level = LogLevel.Information,
        Message = "Broadcast state {State}; reconnects {ReconnectCount}; attempt {RetryAttempt}; downtime {DowntimeSeconds}; live {ElapsedSeconds}")]
    private static partial void LogState(ILogger logger, int state, int reconnectCount, int retryAttempt, double downtimeSeconds, double elapsedSeconds);
}
