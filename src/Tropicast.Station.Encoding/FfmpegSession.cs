using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.Audio;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.Encoding;

internal sealed partial class FfmpegSession : IEncoderSession
{
    private readonly Process _process;
    private readonly TropicastSourceConnection _connection;
    private readonly EncoderOptions _options;
    private readonly CancellationTokenSource _abort = new();
    private readonly PcmQueue _input;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private EncoderSnapshot _snapshot = new(EncoderState.Starting, "Encoding is starting.", 0);
    private long _lastDropLog = long.MinValue;
    private IOException? _failure;
    private bool _stopping;
    private bool _disposed;
    private bool _tokensDisposed;

    internal FfmpegSession(Process process, TropicastSourceConnection connection, EncoderOptions options, ILogger? logger = null)
    {
        _process = process;
        _connection = connection;
        _options = options;
        _input = new PcmQueue(options.Format);
        _logger = logger ?? NullLogger.Instance;
        Completion = Task.Run(RunAsync);
    }

    public EncoderSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public Task Completion { get; }

    public void Submit(PcmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sync)
        {
            if (_stopping || _disposed || _failure is not null)
            {
                throw _failure ?? new IOException("The encoder is no longer accepting audio.");
            }
            if (frame.Format != _options.Format)
            {
                Fail(new EncoderException("PCM format changed or does not match the encoder configuration."));
                throw _failure!;
            }
            foreach (var sample in MemoryMarshal.Cast<byte, float>(frame.Data.Span))
            {
                if (!float.IsFinite(sample))
                {
                    Fail(new EncoderException("PCM contains non-finite samples. Publishing stopped."));
                    throw _failure!;
                }
            }
            long dropped;
            try
            {
                dropped = _input.Enqueue(new(frame.Format, frame.Data.ToArray()));
            }
            catch (EncoderException ex)
            {
                Fail(ex);
                throw;
            }
            // At most one line per drop window: a slow machine would otherwise log at the capture rate.
            if (dropped > 0 && (_lastDropLog == long.MinValue
                || Stopwatch.GetElapsedTime(_lastDropLog) > PcmQueue.DropWindow))
            {
                _lastDropLog = Stopwatch.GetTimestamp();
                var bytesPerMs = _options.Format.SampleRate * _options.Format.BytesPerFrame / 1000.0;
                LogDropped(_logger, _options.CodecName, dropped / bytesPerMs, _input.DroppedBytes / bytesPerMs);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Codec} encoder fell behind: dropped {DroppedMs:F0} ms of the oldest audio to stay live ({TotalMs:F0} ms this session).")]
    private static partial void LogDropped(ILogger logger, string codec, double droppedMs, double totalMs);

    private void Fail(IOException error)
    {
        _failure ??= error;
        _input.Complete();
        if (!_disposed)
        {
            _abort.Cancel();
        }
    }

    private async Task FeedAsync()
    {
        await foreach (var frame in _input.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
        {
            _input.Taken(frame);
            await _process.StandardInput.BaseStream.WriteAsync(frame.Data, _abort.Token).ConfigureAwait(false);
        }
        _process.StandardInput.Close();
    }

    private async Task PublishAsync()
    {
        var bytes = new byte[16384];
        int count;
        while ((count = await _process.StandardOutput.BaseStream.ReadAsync(bytes, _abort.Token).ConfigureAwait(false)) > 0)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await _connection.AudioStream.WriteAsync(bytes.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_abort.IsCancellationRequested)
            {
                throw new EncoderException("Tropicast stopped accepting audio for 10 seconds. Publishing stopped.", isTransient: true);
            }
            var snapshot = Snapshot;
            Volatile.Write(ref _snapshot, new(_stopping ? EncoderState.Stopping : EncoderState.Streaming,
                _stopping ? $"Finishing the {_options.CodecName} stream." : $"Publishing {_options.CodecName} audio to Tropicast.",
                snapshot.EncodedBytes + count));
        }
    }

    private async Task ReadDiagnosticsAsync()
    {
        // FFmpeg sees no endpoint or credentials. Classify bounded lines without retaining native text.
        var chars = new char[1024];
        var pending = "";
        int count;
        while ((count = await _process.StandardError.ReadAsync(chars, _abort.Token).ConfigureAwait(false)) > 0)
        {
            pending += new string(chars, 0, count);
            var lines = pending.Split('\n');
            foreach (var line in lines[..^1])
            {
                if (line.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Encoder not found", StringComparison.OrdinalIgnoreCase))
                {
                    throw new EncoderException(_options.Codec == EncoderCodec.Opus
                        ? "Bundled FFmpeg lacks libopus. Rebuild the documented FFmpeg bundle."
                        : "Bundled FFmpeg lacks libmp3lame. Rebuild the documented FFmpeg bundle.");
                }
                if (line.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    throw new EncoderException("FFmpeg reported an encoding or pipe error. Check the PCM format and bundled encoder.");
                }
            }
            pending = lines[^1];
            if (pending.Length > 4096)
            {
                throw new EncoderException("FFmpeg emitted an oversized diagnostic line.");
            }
        }
    }

    private async Task RunAsync()
    {
        var feed = FeedAsync();
        var output = PublishAsync();
        var diagnostics = ReadDiagnosticsAsync();
        var monitor = _connection.MonitorAsync(_abort.Token);
        var exit = _process.WaitForExitAsync();
        var tasks = new List<Task> { feed, output, diagnostics, monitor, exit };
        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);
                await completed.ConfigureAwait(false);
                if (completed == exit)
                {
                    if (_process.ExitCode != 0)
                    {
                        throw new EncoderException("FFmpeg exited unexpectedly. The publisher will need a new session.", isTransient: true);
                    }
                    if (!_stopping)
                    {
                        throw new EncoderException("FFmpeg ended unexpectedly while publishing.", isTransient: true);
                    }
                    await feed.ConfigureAwait(false);
                    await output.ConfigureAwait(false);
                    await diagnostics.ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            lock (_sync)
            {
                if (ex is not OperationCanceledException || !_stopping)
                {
                    _failure ??= ex is TropicastSourceException or EncoderException
                        ? (IOException)ex : new IOException("Encoding or Tropicast connection failed. Publishing stopped.");
                }
            }
        }
        finally
        {
            _abort.Cancel();
            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (_process.HasExited)
                    {
                        // Normal exit won the race with termination.
                    }
                }
                await exit.ConfigureAwait(false);
                // Observe every pipe task before disposing its streams.
                foreach (var task in new[] { feed, output, diagnostics, monitor })
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
                    {
                        // First failure is preserved; cancellation is expected after stop/abort.
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _failure = new EncoderException("FFmpeg could not be terminated. Restart the app before retrying.");
            }
            finally
            {
                try
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    _failure = new EncoderException("The Tropicast connection could not be released cleanly. Restart the app before retrying.");
                }
                finally
                {
                    lock (_sync)
                    {
                        _disposed = true;
                        _process.Dispose();
                    }
                    Volatile.Write(ref _snapshot, new(_failure is null ? EncoderState.Stopped : EncoderState.Failed,
                        _failure?.Message ?? "Publishing stopped.", Snapshot.EncodedBytes));
                }
            }
        }
        if (_failure is not null)
        {
            throw _failure;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_stopping)
            {
                _stopping = true;
                _input.Complete();
                if (!_disposed && _failure is null)
                {
                    Volatile.Write(ref _snapshot, Snapshot with { State = EncoderState.Stopping, Message = $"Finishing the {_options.CodecName} stream." });
                }
            }
        }
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                Fail(new EncoderException("FFmpeg did not stop within five seconds and was terminated."));
            }
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _abort.Cancel();
                }
            }
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Preserve caller cancellation after observing the explicit session failure.
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (Completion.IsCompleted)
            {
                lock (_sync)
                {
                    if (!_tokensDisposed)
                    {
                        _abort.Dispose();
                        _tokensDisposed = true;
                    }
                }
                GC.SuppressFinalize(this);
            }
        }
    }
}
