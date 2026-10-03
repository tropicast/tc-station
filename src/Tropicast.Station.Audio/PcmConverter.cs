using System.Buffers.Binary;

namespace Tropicast.Station.Audio;

/// <summary>
/// Stateful windowed-sinc resampler with 32-frame lookahead and an anti-alias low-pass filter.
/// Output is float32 mono/stereo. Mono is duplicated to stereo; multichannel input is averaged
/// for mono or averaged by alternating channel indices for stereo (not a surround-layout mixer).
/// Keep one instance per capture session. Flush only for a natural end of a finite input.
/// </summary>
public sealed class PcmConverter
{
    private const int Radius = 32;
    private readonly AudioFormat _source;
    private readonly AudioFormat _target;
    private readonly List<float[]> _buffer = [];
    private long _bufferStart;
    private long _inputFrames;
    private long _outputFrames;
    private bool _flushed;

    public PcmConverter(AudioFormat source, AudioFormat target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Encoding != PcmEncoding.Float32 || target.Channels > 2)
        {
            throw new ArgumentException("Encoder output must be float32 mono or stereo.", nameof(target));
        }

        _source = source;
        _target = target;
    }

    public PcmFrame? Convert(PcmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_flushed || frame.Format != _source)
        {
            throw new InvalidOperationException("The source format changed or the converter has already been flushed.");
        }

        var bytes = frame.Data.Span;
        var sampleBytes = _source.BytesPerFrame / _source.Channels;
        for (var index = 0; index < frame.FrameCount; index++)
        {
            var samples = new float[_source.Channels];
            for (var channel = 0; channel < samples.Length; channel++)
            {
                var offset = (index * samples.Length + channel) * sampleBytes;
                samples[channel] = _source.Encoding == PcmEncoding.Signed16
                    ? BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) / 32768f
                    : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
                if (!float.IsFinite(samples[channel]))
                {
                    throw new IOException("Capture returned non-finite PCM samples.");
                }
            }

            var mapped = new float[_target.Channels];
            for (var channel = 0; channel < mapped.Length; channel++)
            {
                if (samples.Length == 1)
                {
                    mapped[channel] = samples[0];
                }
                else
                {
                    var count = 0;
                    for (var sourceChannel = _target.Channels == 1 ? 0 : channel; sourceChannel < samples.Length;
                        sourceChannel += _target.Channels)
                    {
                        mapped[channel] += samples[sourceChannel];
                        count++;
                    }

                    mapped[channel] /= count;
                }
            }

            _buffer.Add(mapped);
        }

        _inputFrames += frame.FrameCount;
        return Produce(flush: false);
    }

    public PcmFrame? Flush()
    {
        if (_flushed)
        {
            throw new InvalidOperationException("The converter has already been flushed.");
        }

        _flushed = true;
        return Produce(flush: true);
    }

    private PcmFrame? Produce(bool flush)
    {
        var output = new List<float>();
        var cutoff = 0.94 * Math.Min(1.0, (double)_target.SampleRate / _source.SampleRate);
        while (true)
        {
            var position = (double)_outputFrames * _source.SampleRate / _target.SampleRate;
            if (position >= _inputFrames || (!flush && position + Radius >= _inputFrames))
            {
                break;
            }

            var center = (long)Math.Floor(position);
            for (var channel = 0; channel < _target.Channels; channel++)
            {
                if (_source.SampleRate == _target.SampleRate)
                {
                    output.Add(_buffer[(int)(center - _bufferStart)][channel]);
                    continue;
                }

                double sum = 0, weights = 0;
                for (var index = center - Radius + 1; index <= center + Radius; index++)
                {
                    var distance = position - index;
                    var x = Math.PI * cutoff * distance;
                    var sinc = Math.Abs(x) < 1e-10 ? 1 : Math.Sin(x) / x;
                    var window = 0.5 * (1 + Math.Cos(Math.PI * distance / Radius));
                    var weight = cutoff * sinc * window;
                    weights += weight;
                    if (index >= 0 && index < _inputFrames)
                    {
                        sum += _buffer[(int)(index - _bufferStart)][channel] * weight;
                    }
                }

                output.Add((float)(sum / weights));
            }

            _outputFrames++;
        }

        var retainFrom = Math.Max(0, (long)Math.Floor((double)_outputFrames * _source.SampleRate / _target.SampleRate) - Radius);
        var remove = (int)Math.Min(_buffer.Count, retainFrom - _bufferStart);
        if (remove > 0)
        {
            _buffer.RemoveRange(0, remove);
            _bufferStart += remove;
        }

        if (output.Count == 0)
        {
            return null;
        }

        var data = new byte[output.Count * 4];
        for (var i = 0; i < output.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), BitConverter.SingleToInt32Bits(output[i]));
        }

        return new PcmFrame(_target, data);
    }
}
