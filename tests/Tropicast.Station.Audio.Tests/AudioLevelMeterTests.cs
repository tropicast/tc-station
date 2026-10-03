using System.Buffers.Binary;
using System.Diagnostics;

namespace Tropicast.Station.Audio.Tests;

public sealed class AudioLevelMeterTests
{
    [Theory]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    public void Sine_peak_and_rms_match_dbfs_per_channel(int rate, int channels)
    {
        var meter = new AudioLevelMeter();
        var format = new AudioFormat(rate, channels);
        meter.Start(format);
        meter.Process(Signal(format, rate / 25, (i, channel) =>
            (float)((channel == 0 ? 0.5 : 0.25) * Math.Sin(2 * Math.PI * 1000 * i / rate))));
        var snapshot = meter.Read();
        Assert.True(snapshot.IsActive);
        Assert.False(snapshot.IsSilent);
        for (var channel = 0; channel < channels; channel++)
        {
            var amplitude = channel == 0 ? 0.5 : 0.25;
            Assert.Equal(20 * Math.Log10(amplitude), snapshot.Channels[channel].PeakDb, precision: 2);
            Assert.Equal(20 * Math.Log10(amplitude / Math.Sqrt(2)), snapshot.Channels[channel].RmsDb, precision: 5);
            Assert.False(snapshot.Channels[channel].IsClipping);
        }
    }

    [Fact]
    public void Rms_is_sample_weighted_across_unequal_packets_and_peak_uses_all_packets()
    {
        var meter = new AudioLevelMeter();
        var format = new AudioFormat(48000, 2);
        meter.Start(format);
        meter.Process(Signal(format, 100, (_, channel) => channel == 0 ? -0.5f : 0));
        meter.Process(Signal(format, 300, (_, channel) => channel == 0 ? 0 : 0.25f));
        var levels = meter.Read().Channels;
        Assert.Equal(20 * Math.Log10(0.5), levels[0].PeakDb, precision: 6);
        Assert.Equal(20 * Math.Log10(0.25), levels[0].RmsDb, precision: 6);
        Assert.Equal(20 * Math.Log10(0.25), levels[1].PeakDb, precision: 6);
        Assert.Equal(20 * Math.Log10(0.25 * Math.Sqrt(0.75)), levels[1].RmsDb, precision: 6);
        Assert.All(meter.Read().Channels, level =>
        {
            Assert.Equal(AudioLevelMeter.FloorDb, level.PeakDb);
            Assert.Equal(AudioLevelMeter.FloorDb, level.RmsDb);
        });
    }

    [Fact]
    public void Clip_threshold_includes_negative_samples_holds_two_seconds_and_retriggers()
    {
        var clock = new Clock();
        var meter = new AudioLevelMeter(clock);
        var format = new AudioFormat(48000, 2);
        var below = float.BitDecrement((float)Math.Pow(10, -0.1 / 20));
        var above = float.BitIncrement(below);
        meter.Start(format);
        meter.Process(Signal(format, 1, (_, channel) => channel == 0 ? -above : below));
        var levels = meter.Read().Channels;
        Assert.True(levels[0].IsClipping);
        Assert.False(levels[1].IsClipping);
        clock.Advance(1.999);
        Assert.True(meter.Read().Channels[0].IsClipping);
        clock.Advance(0.001);
        Assert.False(meter.Read().Channels[0].IsClipping);
        meter.Process(Signal(format, 1, (_, _) => 1.2f));
        levels = meter.Read().Channels;
        Assert.All(levels, level => Assert.True(level.IsClipping));
        Assert.True(levels[0].PeakDb > 0);
    }

    [Fact]
    public void Silence_includes_no_packet_endpoints_and_clears_on_signal_in_either_channel()
    {
        var clock = new Clock();
        var meter = new AudioLevelMeter(clock);
        var format = new AudioFormat(48000, 2);
        meter.Configure(-50, TimeSpan.FromSeconds(3));
        meter.Start(format);
        clock.Advance(2.999);
        Assert.False(meter.Read().IsSilent);
        clock.Advance(0.001);
        Assert.True(meter.Read().IsSilent);
        meter.Process(Signal(format, 100, (_, channel) => channel == 1 ? 0.01f : 0));
        Assert.False(meter.Read().IsSilent);
        clock.Advance(2);
        meter.Process(Signal(format, 100, (_, _) => 0.001f));
        Assert.False(meter.Read().IsSilent);
        clock.Advance(1);
        Assert.True(meter.Read().IsSilent);
    }

    [Fact]
    public void Silence_uses_rms_not_single_peak_and_configuration_restarts_countdown()
    {
        var clock = new Clock();
        var meter = new AudioLevelMeter(clock);
        var format = new AudioFormat(48000, 1);
        meter.Configure(-20, TimeSpan.FromSeconds(1));
        meter.Start(format);
        clock.Advance(1);
        // One full-scale sample in 480 samples has RMS below -20 dBFS.
        meter.Process(Signal(format, 480, (i, _) => i == 0 ? 1 : 0));
        Assert.True(meter.Read().IsSilent);
        meter.Configure(-50, TimeSpan.FromSeconds(2));
        Assert.False(meter.Read().IsSilent);
        clock.Advance(2);
        Assert.True(meter.Read().IsSilent);
        meter.Process(Signal(format, 100, (_, _) => 0.01f));
        Assert.False(meter.Read().IsSilent);
    }

    [Fact]
    public void Stop_and_new_capture_clear_levels_hold_and_silence()
    {
        var clock = new Clock();
        var meter = new AudioLevelMeter(clock);
        var format = new AudioFormat(48000, 2);
        meter.Start(format);
        meter.Process(Signal(format, 10, (_, _) => 1));
        meter.Stop();
        clock.Advance(10);
        var stopped = meter.Read();
        Assert.False(stopped.IsActive);
        Assert.Empty(stopped.Channels);
        Assert.False(stopped.IsSilent);
        meter.Start(new(44100, 1));
        var restarted = meter.Read();
        Assert.False(restarted.IsSilent);
        Assert.False(Assert.Single(restarted.Channels).IsClipping);
        Assert.Equal(AudioLevelMeter.FloorDb, restarted.Channels[0].PeakDb);
    }

    [Fact]
    public void Invalid_formats_settings_and_samples_are_explicit_errors()
    {
        var meter = new AudioLevelMeter();
        Assert.Throws<ArgumentException>(() => meter.Start(new(48000, 4)));
        Assert.Throws<ArgumentException>(() => meter.Start(new(48000, 1, PcmEncoding.Signed16)));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Configure(double.NaN, TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Configure(-91, TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Configure(-50, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Configure(-50, TimeSpan.FromSeconds(61)));
        meter.Start(new(48000, 1));
        Assert.Throws<IOException>(() => meter.Process(Signal(new(44100, 1), 1, (_, _) => 0)));
        Assert.Throws<IOException>(() => meter.Process(Signal(new(48000, 1), 1, (_, _) => float.NaN)));
    }

    [Fact]
    public void Forty_millisecond_stereo_windows_can_be_processed_faster_than_real_time()
    {
        var meter = new AudioLevelMeter();
        var format = new AudioFormat(48000, 2);
        var frame = Signal(format, 1920, (i, channel) => (float)(0.2 * Math.Sin(i + channel)));
        meter.Start(format);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 25; i++)
        {
            meter.Process(frame);
            Assert.Equal(2, meter.Read().Channels.Count);
        }
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"One second of PCM took {timer.Elapsed} to meter.");
    }

    private static PcmFrame Signal(AudioFormat format, int frames, Func<int, int, float> sample)
    {
        var bytes = new byte[format.BytesPerFrame * frames];
        for (var i = 0; i < frames; i++)
        {
            for (var channel = 0; channel < format.Channels; channel++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * format.Channels + channel) * 4), sample(i, channel));
            }
        }
        return new(format, bytes);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
