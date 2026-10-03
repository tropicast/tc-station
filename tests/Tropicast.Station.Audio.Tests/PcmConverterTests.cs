using System.Buffers.Binary;

namespace Tropicast.Station.Audio.Tests;

public sealed class PcmConverterTests
{
    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(48000, 16000)]
    public void Streaming_resampling_preserves_duration_and_tone_and_is_chunk_independent(int inputRate, int outputRate)
    {
        var source = new AudioFormat(inputRate, 1);
        var frame = Signal(source, inputRate / 5, 1000);
        var one = ConvertAll(source, new(outputRate, 2), frame, frame.FrameCount);
        var chunked = ConvertAll(source, new(outputRate, 2), frame, 137);
        Assert.Equal((int)Math.Ceiling((double)frame.FrameCount * outputRate / inputRate) * 2, one.Length);
        Assert.Equal(one, chunked);
        for (var i = 0; i < one.Length; i += 2)
        {
            Assert.Equal(one[i], one[i + 1]);
        }

        var errors = new List<double>();
        for (var i = 100; i < one.Length / 2 - 100; i++)
        {
            errors.Add(Math.Pow(one[i * 2] - 0.2 * Math.Sin(2 * Math.PI * 1000 * i / outputRate), 2));
        }

        Assert.InRange(Math.Sqrt(errors.Average()), 0, 0.002);
    }

    [Fact]
    public void Downsampling_suppresses_frequencies_above_output_nyquist()
    {
        var source = new AudioFormat(48000, 1);
        var highTone = Signal(source, 9600, 12000);
        var converted = ConvertAll(source, new(16000, 1), highTone, 511);
        var interior = converted.Skip(100).Take(converted.Length - 200);
        Assert.InRange(Math.Sqrt(interior.Average(x => (double)x * x)), 0, 0.005);
    }

    [Fact]
    public void Signed16_scaling_and_stereo_to_mono_mapping_are_correct()
    {
        var source = new AudioFormat(48000, 2, PcmEncoding.Signed16);
        var bytes = new byte[200 * source.BytesPerFrame];
        for (var i = 0; i < 200; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), short.MinValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), 16384);
        }

        var converted = ConvertAll(source, new(48000, 1), new(source, bytes), 17);
        Assert.Equal(200, converted.Length);
        Assert.All(converted, sample => Assert.Equal(-0.25f, sample));
    }

    [Fact]
    public void Multichannel_mapping_is_documented_index_based_average()
    {
        var source = new AudioFormat(48000, 4);
        var bytes = new byte[100 * source.BytesPerFrame];
        for (var i = 0; i < 400; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), BitConverter.SingleToInt32Bits(i % 4 * 0.1f));
        }

        var stereo = ConvertAll(source, new(48000, 2), new(source, bytes), 10);
        Assert.Equal(0.1f, stereo[0], precision: 6);
        Assert.Equal(0.2f, stereo[1], precision: 6);
    }

    [Fact]
    public void Invalid_pcm_and_format_changes_are_explicit_errors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFormat(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFormat(48000, 0));
        Assert.Throws<ArgumentException>(() => new PcmFrame(new(48000, 2), new byte[7]));
        Assert.Throws<ArgumentException>(() => new PcmConverter(new(48000, 2), new(48000, 2, PcmEncoding.Signed16)));
        var converter = new PcmConverter(new(48000, 1), new(48000, 2));
        Assert.Throws<InvalidOperationException>(() => converter.Convert(Signal(new(44100, 1), 100, 440)));
        var bytes = BitConverter.GetBytes(float.NaN);
        Assert.Throws<IOException>(() => converter.Convert(new(new(48000, 1), bytes)));
        converter.Flush();
        Assert.Throws<InvalidOperationException>(() => converter.Flush());
    }

    private static PcmFrame Signal(AudioFormat format, int frames, double frequency)
    {
        var data = new byte[frames * format.BytesPerFrame];
        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(0.2 * Math.Sin(2 * Math.PI * frequency * i / format.SampleRate));
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), BitConverter.SingleToInt32Bits(sample));
        }

        return new(format, data);
    }

    private static float[] ConvertAll(AudioFormat source, AudioFormat target, PcmFrame frame, int chunkSize)
    {
        var converter = new PcmConverter(source, target);
        var result = new List<float>();
        for (var offset = 0; offset < frame.FrameCount; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, frame.FrameCount - offset);
            Add(converter.Convert(new(source, frame.Data.Slice(offset * source.BytesPerFrame, count * source.BytesPerFrame))), result);
        }

        Add(converter.Flush(), result);
        return result.ToArray();
    }

    private static void Add(PcmFrame? frame, List<float> result)
    {
        if (frame is null)
        {
            return;
        }

        for (var i = 0; i < frame.Data.Length; i += 4)
        {
            result.Add(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(frame.Data.Span[i..])));
        }
    }
}
