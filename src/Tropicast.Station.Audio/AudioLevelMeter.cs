using System.Buffers.Binary;

namespace Tropicast.Station.Audio;

public sealed record ChannelLevel(double PeakDb, double RmsDb, bool IsClipping);
public sealed record AudioLevelSnapshot(bool IsActive, IReadOnlyList<ChannelLevel> Channels, bool IsSilent);

/// <summary>
/// Accumulates normalized PCM on the capture thread; the UI reads a window at 25 Hz.
/// Silence uses a monotonic clock, including endpoints that deliver no packets when silent.
/// </summary>
public sealed class AudioLevelMeter(TimeProvider? clock = null)
{
    public const double FloorDb = -90;
    private static readonly double ClipAmplitude = Math.Pow(10, -0.1 / 20);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _sync = new();
    private AudioFormat? _format;
    private readonly double[] _peaks = new double[2];
    private readonly double[] _squares = new double[2];
    private readonly long?[] _clippedAt = new long?[2];
    private long _frames;
    private long _lastSignal;
    private double _silenceDb = -50;
    private TimeSpan _silenceDuration = TimeSpan.FromSeconds(5);

    public void Configure(double silenceDb, TimeSpan silenceDuration)
    {
        if (!double.IsFinite(silenceDb) || silenceDb is < FloorDb or > -10)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceDb));
        }
        if (silenceDuration < TimeSpan.FromSeconds(1) || silenceDuration > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(silenceDuration));
        }
        lock (_sync)
        {
            _silenceDb = silenceDb;
            _silenceDuration = silenceDuration;
            _lastSignal = _clock.GetTimestamp();
        }
    }

    public void Start(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Encoding != PcmEncoding.Float32 || format.Channels > 2)
        {
            throw new ArgumentException("Meters require normalized float32 mono/stereo PCM.", nameof(format));
        }
        lock (_sync)
        {
            Reset();
            _format = format;
            _lastSignal = _clock.GetTimestamp();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _format = null;
            Reset();
        }
    }

    public void Process(PcmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sync)
        {
            if (_format is null)
            {
                return; // A reader may finish a packet while capture is being stopped.
            }
            if (frame.Format != _format)
            {
                throw new IOException("Meter PCM format changed during capture.");
            }
            var now = _clock.GetTimestamp();
            Span<double> frameSquares = stackalloc double[2];
            frameSquares.Clear();
            var bytes = frame.Data.Span;
            for (var i = 0; i < frame.FrameCount; i++)
            {
                for (var channel = 0; channel < _format.Channels; channel++)
                {
                    var sample = BinaryPrimitives.ReadSingleLittleEndian(bytes[((i * _format.Channels + channel) * 4)..]);
                    if (!float.IsFinite(sample))
                    {
                        throw new IOException("Meter received non-finite PCM samples.");
                    }
                    var magnitude = Math.Abs((double)sample);
                    _peaks[channel] = Math.Max(_peaks[channel], magnitude);
                    var square = magnitude * magnitude;
                    _squares[channel] += square;
                    frameSquares[channel] += square;
                    if (magnitude >= ClipAmplitude)
                    {
                        _clippedAt[channel] = now;
                    }
                }
            }
            _frames += frame.FrameCount;
            var silencePower = Math.Pow(10, _silenceDb / 10);
            for (var channel = 0; channel < _format.Channels; channel++)
            {
                if (frameSquares[channel] / frame.FrameCount >= silencePower)
                {
                    _lastSignal = now;
                }
            }
        }
    }

    public AudioLevelSnapshot Read()
    {
        lock (_sync)
        {
            if (_format is null)
            {
                return new(false, [], false);
            }
            var now = _clock.GetTimestamp();
            var channels = new ChannelLevel[_format.Channels];
            for (var channel = 0; channel < channels.Length; channel++)
            {
                channels[channel] = new(ToDb(_peaks[channel]),
                    ToDb(_frames == 0 ? 0 : Math.Sqrt(_squares[channel] / _frames)),
                    _clippedAt[channel] is { } clipped && _clock.GetElapsedTime(clipped, now) < TimeSpan.FromSeconds(2));
            }
            Array.Clear(_peaks);
            Array.Clear(_squares);
            _frames = 0;
            return new(true, channels, _clock.GetElapsedTime(_lastSignal, now) >= _silenceDuration);
        }
    }

    private static double ToDb(double amplitude) => amplitude == 0 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(amplitude));

    private void Reset()
    {
        Array.Clear(_peaks);
        Array.Clear(_squares);
        Array.Clear(_clippedAt);
        _frames = 0;
    }
}
