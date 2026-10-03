using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Tropicast.Station.Audio.Windows;

/// <summary>Copies NAudio's reusable callback buffer into a bounded, single-consumer PCM stream.</summary>
internal sealed class WasapiCaptureSession : IAudioCaptureSession
{
    // Two seconds by byte budget, also bounded by packet count for unusually small callbacks.
    private const int PacketCapacity = 256;
    private readonly IWasapiSource _source;
    private readonly Channel<PcmFrame> _frames = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(PacketCapacity)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
    });
    private readonly object _sync = new();
    private readonly long _byteLimit;
    private long _queuedBytes;
    private int _reader;
    private bool _stopping;
    private bool _disposed;
    private Task? _disposeTask;

    internal WasapiCaptureSession(IWasapiSource source)
    {
        _source = source;
        _byteLimit = (long)source.Format.SampleRate * source.Format.BytesPerFrame * 2;
        source.DataAvailable += OnData;
        source.Stopped += OnStopped;
    }

    internal void Start()
    {
        try
        {
            _source.Start();
        }
        catch
        {
            _source.DataAvailable -= OnData;
            _source.Stopped -= OnStopped;
            _source.Dispose();
            _disposed = true;
            _disposeTask = Task.CompletedTask;
            throw;
        }
    }

    private void OnData(object? sender, WasapiDataEventArgs e)
    {
        lock (_sync)
        {
            if (_stopping || _disposed || e.Count == 0)
            {
                return;
            }

            if (e.Count < 0 || e.Count > e.Buffer.Length || e.Count % _source.Format.BytesPerFrame != 0)
            {
                Fail(new IOException("Windows audio returned a malformed PCM packet."));
                return;
            }

            var queued = Interlocked.Add(ref _queuedBytes, e.Count);
            if (queued > _byteLimit)
            {
                Interlocked.Add(ref _queuedBytes, -e.Count);
                Fail(new IOException("Audio capture overrun: the consumer fell more than two seconds behind. Capture stopped; retry with lower load."));
                return;
            }

            var frame = new PcmFrame(_source.Format, e.Buffer.AsMemory(0, e.Count).ToArray());
            if (!_frames.Writer.TryWrite(frame))
            {
                Interlocked.Add(ref _queuedBytes, -e.Count);
                Fail(new IOException("Audio capture overrun: the PCM queue is full. Capture stopped; retry with lower load."));
            }
        }
    }

    private void Fail(IOException error)
    {
        _stopping = true;
        _frames.Writer.TryComplete(error);
        // StopRecording is a non-blocking request, safe from the native capture callback.
        _source.Stop();
    }

    private void OnStopped(object? sender, WasapiStoppedEventArgs e)
    {
        lock (_sync)
        {
            _stopping = true;
            _frames.Writer.TryComplete(e.Exception is null ? null : WindowsAudioErrors.Describe(e.Exception));
        }
    }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reader, 1) != 0)
        {
            throw new InvalidOperationException("A WASAPI session supports only one reader.");
        }

        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes, -frame.Data.Length);
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
            }
        }
        finally
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_disposed && !_stopping)
            {
                _stopping = true;
                _source.Stop();
            }

            // Do not wait for a native event to unblock the consumer.
            _frames.Writer.TryComplete();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _stopping = true;
                _disposed = true;
                _frames.Writer.TryComplete();
                _source.DataAvailable -= OnData;
                _source.Stopped -= OnStopped;
                // Dispose joins NAudio's capture thread. Never join it on a callback or UI thread.
                _disposeTask = Task.Run(_source.Dispose);
            }

            return new ValueTask(_disposeTask);
        }
    }
}
