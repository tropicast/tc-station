using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Tropicast.Station.Audio.MacOS;

internal sealed class MacCaptureSession : IAudioCaptureSession
{
    private readonly IMacAudioSource _source;
    private readonly object _sync = new();
    private readonly Channel<PcmFrame> _frames = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(256)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
    });
    private long _queuedBytes;
    private int _reader;
    private bool _stopping;
    private Task? _stopTask;

    internal MacCaptureSession(IMacAudioSource source)
    {
        _source = source;
        source.DataAvailable += OnData;
        source.Failed += OnFailure;
    }

    internal void Start()
    {
        try
        {
            _source.Start();
        }
        catch
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private void OnData(object? sender, MacPcmEventArgs e)
    {
        lock (_sync)
        {
            if (_stopping || e.Data.Length == 0)
            {
                return;
            }
            if (e.Data.Length % _source.Format.BytesPerFrame != 0)
            {
                Fail(new IOException("macOS returned an incomplete PCM packet."));
                return;
            }
            if (_queuedBytes + e.Data.Length > (long)_source.Format.SampleRate * _source.Format.BytesPerFrame * 2)
            {
                Fail(new IOException("macOS audio capture overrun: the consumer fell more than two seconds behind."));
                return;
            }
            var frame = new PcmFrame(_source.Format, e.Data.ToArray());
            if (!_frames.Writer.TryWrite(frame))
            {
                Fail(new IOException("macOS audio capture overrun: the PCM queue is full."));
                return;
            }
            _queuedBytes += e.Data.Length;
        }
    }

    private void OnFailure(object? sender, MacFailureEventArgs e)
    {
        lock (_sync)
        {
            if (!_stopping)
            {
                Fail(e.Error);
            }
        }
    }

    private void Fail(IOException error)
    {
        _frames.Writer.TryComplete(error);
        BeginStop();
    }

    private Task BeginStop()
    {
        _stopping = true;
        _frames.Writer.TryComplete();
        return _stopTask ??= Task.Run(() =>
        {
            // Native disposal drains callback queues; never block a callback or UI thread.
            _source.Dispose();
            _source.DataAvailable -= OnData;
            _source.Failed -= OnFailure;
        });
    }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reader, 1) != 0)
        {
            throw new InvalidOperationException("A macOS capture session supports only one reader.");
        }
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_sync)
                {
                    _queuedBytes -= frame.Data.Length;
                }
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
            return BeginStop();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}
