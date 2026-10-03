using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Tropicast.Station.Audio.Linux;

internal sealed class PulseCaptureSession : IAudioCaptureSession
{
    private readonly AudioFormat _format;
    private readonly Stream _input;
    private readonly Action _stop;
    private readonly Func<Task<int>> _exit;
    private readonly Action _release;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<PcmFrame> _frames = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(100)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
    });
    private readonly Task _readerTask;
    private readonly object _sync = new();
    private Task? _disposeTask;
    private int _reader;

    internal PulseCaptureSession(AudioDevice device)
    {
        _format = device.NativeFormat;
        var child = new PulseProcess("parec",
            "--raw", $"--device={device.Id}", "--format=float32le",
            $"--rate={_format.SampleRate.ToString(CultureInfo.InvariantCulture)}",
            $"--channels={_format.Channels.ToString(CultureInfo.InvariantCulture)}",
            "--latency-msec=40", "--process-time-msec=20",
            "--client-name=Tropicast Station", "--stream-name=Tropicast capture",
            "--property=media.role=production", "--property=stream.dont-move=true");
        _input = child.Output;
        _stop = child.Stop;
        _exit = () => child.WaitAsync(CancellationToken.None);
        _release = child.Dispose;
        _readerTask = ReadPacketsAsync();
    }

    internal PulseCaptureSession(AudioFormat format, Stream input, Action stop, Func<Task<int>> exit, Action release)
    {
        _format = format;
        _input = input;
        _stop = stop;
        _exit = exit;
        _release = release;
        _readerTask = ReadPacketsAsync();
    }

    private async Task ReadPacketsAsync()
    {
        Exception? error = null;
        try
        {
            var packetSize = Math.Max(1, _format.SampleRate / 50) * _format.BytesPerFrame;
            var bytes = new byte[packetSize];
            var filled = 0;
            while (!_shutdown.IsCancellationRequested)
            {
                var count = await _input.ReadAsync(bytes.AsMemory(filled), _shutdown.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    var exitCode = await _exit().ConfigureAwait(false);
                    throw new IOException(filled != 0 ? "PulseAudio returned an incomplete PCM packet."
                        : exitCode != 0 ? "PulseAudio capture failed. Check source availability, recording permissions and the audio server."
                        : "PulseAudio capture ended unexpectedly. The selected source may have disconnected.");
                }

                filled += count;
                if (filled == bytes.Length)
                {
                    if (!_frames.Writer.TryWrite(new(_format, bytes)))
                    {
                        throw new IOException("PulseAudio capture overrun: the PCM consumer fell two seconds behind. Capture stopped; reduce system load.");
                    }

                    bytes = new byte[packetSize];
                    filled = 0;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException ex) when (!_shutdown.IsCancellationRequested)
        {
            error = ex;
        }
        catch (IOException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            try
            {
                _stop();
            }
            catch (IOException ex)
            {
                error ??= ex;
            }
            finally
            {
                _frames.Writer.TryComplete(error);
            }
        }
    }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reader, 1) != 0)
        {
            throw new InvalidOperationException("A PulseAudio session supports only one consumer.");
        }

        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
            }
        }
        finally
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return;
            }

            _shutdown.Cancel();
            _stop();
        }

        await _readerTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposeTask ??= Task.Run(async () =>
            {
                try
                {
                    _shutdown.Cancel();
                    _stop();
                    await _readerTask.ConfigureAwait(false);
                    await _exit().ConfigureAwait(false);
                }
                finally
                {
                    _release();
                    _shutdown.Dispose();
                }
            });
            return new(_disposeTask);
        }
    }
}
