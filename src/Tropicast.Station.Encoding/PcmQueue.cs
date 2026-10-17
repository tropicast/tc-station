using System.Threading.Channels;
using Tropicast.Station.Audio;

namespace Tropicast.Station.Encoding;

/// <summary>
/// PCM waiting for FFmpeg, at most <see cref="MaxDelay"/> of audio, so listeners stay close to live. When FFmpeg falls
/// behind, the oldest audio is dropped; more than <see cref="MaxDropped"/> dropped within <see cref="DropWindow"/> is
/// an overrun. A single frame longer than the budget is still accepted once the queue is empty.
/// </summary>
internal sealed class PcmQueue
{
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan MaxDropped = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(10);

    // Several readers: Enqueue takes the oldest frames out when it drops them.
    private readonly Channel<PcmFrame> _channel = Channel.CreateUnbounded<PcmFrame>(new UnboundedChannelOptions
    {
        SingleReader = false, SingleWriter = true, AllowSynchronousContinuations = false,
    });
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly long _maxBytes;
    private readonly long _maxDroppedBytes;
    private long _queuedBytes;
    private long _windowStart;
    private long _windowDropped;

    internal PcmQueue(AudioFormat format, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        var bytesPerSecond = (long)format.SampleRate * format.BytesPerFrame;
        _maxBytes = (long)(bytesPerSecond * MaxDelay.TotalSeconds);
        _maxDroppedBytes = (long)(bytesPerSecond * MaxDropped.TotalSeconds);
        _windowStart = _time.GetTimestamp();
    }

    internal ChannelReader<PcmFrame> Reader => _channel.Reader;

    /// <summary>Total audio dropped so far, in bytes.</summary>
    internal long DroppedBytes { get; private set; }

    /// <summary>Queues a copy-owned frame; returns the bytes dropped to make room for it.</summary>
    /// <exception cref="EncoderException">Too much audio was dropped recently: FFmpeg cannot keep up.</exception>
    internal long Enqueue(PcmFrame frame)
    {
        lock (_sync)
        {
            long dropped = 0;
            while (_queuedBytes > 0 && _queuedBytes + frame.Data.Length > _maxBytes && _channel.Reader.TryRead(out var oldest))
            {
                _queuedBytes -= oldest.Data.Length;
                dropped += oldest.Data.Length;
            }
            if (dropped > 0)
            {
                DroppedBytes += dropped;
                if (_time.GetElapsedTime(_windowStart) > DropWindow)
                {
                    _windowStart = _time.GetTimestamp();
                    _windowDropped = 0;
                }
                _windowDropped += dropped;
                if (_windowDropped > _maxDroppedBytes)
                {
                    throw new EncoderException(
                        "Encoder overrun: more than one second of audio dropped in ten seconds. Publishing stopped.");
                }
            }
            if (!_channel.Writer.TryWrite(frame))
            {
                throw new EncoderException("The encoder is no longer accepting audio.");
            }
            _queuedBytes += frame.Data.Length;
            return dropped;
        }
    }

    /// <summary>Called by the reader for each frame it takes.</summary>
    internal void Taken(PcmFrame frame)
    {
        lock (_sync)
        {
            _queuedBytes -= frame.Data.Length;
        }
    }

    internal void Complete() => _channel.Writer.TryComplete();
}
