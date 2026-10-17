using Tropicast.Station.Audio;

namespace Tropicast.Station.Encoding.Tests;

public sealed class PcmQueueTests
{
    private static readonly AudioFormat Format = new(48000, 2);
    private static readonly int BytesPerMs = 48 * 8;

    private static PcmFrame Frame(int ms) => new(Format, new byte[ms * BytesPerMs]);

    [Fact]
    public void Holds_at_most_200_ms_and_drops_the_oldest_audio()
    {
        var queue = new PcmQueue(Format, new ManualTime());
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(0, queue.Enqueue(Frame(20)));
        }
        var newest = Frame(20);
        Assert.Equal(20 * BytesPerMs, queue.Enqueue(newest));
        var queued = new List<PcmFrame>();
        while (queue.Reader.TryRead(out var frame))
        {
            queued.Add(frame);
        }
        Assert.Equal(10, queued.Count);
        Assert.Same(newest, queued[^1]);
    }

    [Fact]
    public void A_frame_longer_than_the_budget_is_accepted_once_the_queue_is_empty()
    {
        var queue = new PcmQueue(Format, new ManualTime());
        queue.Enqueue(Frame(20));
        Assert.Equal(20 * BytesPerMs, queue.Enqueue(Frame(300)));
        Assert.True(queue.Reader.TryRead(out var only));
        Assert.Equal(300 * BytesPerMs, only.Data.Length);
    }

    [Fact]
    public void Taken_frames_no_longer_count()
    {
        var queue = new PcmQueue(Format, new ManualTime());
        for (var i = 0; i < 10; i++)
        {
            queue.Enqueue(Frame(20));
            Assert.True(queue.Reader.TryRead(out var frame));
            queue.Taken(frame);
        }
        Assert.Equal(0, queue.Enqueue(Frame(200)));
        Assert.Equal(0, queue.DroppedBytes);
    }

    [Fact]
    public void More_than_one_second_dropped_within_ten_seconds_is_an_overrun()
    {
        var time = new ManualTime();
        var queue = new PcmQueue(Format, time);
        queue.Enqueue(Frame(200));
        // 200 ms dropped per call: five fit in the window, the sixth does not.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(200 * BytesPerMs, queue.Enqueue(Frame(200)));
            time.Advance(TimeSpan.FromMilliseconds(500));
        }
        var error = Assert.Throws<EncoderException>(() => queue.Enqueue(Frame(200)));
        Assert.Contains("overrun", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Drops_spread_over_time_are_not_an_overrun()
    {
        var time = new ManualTime();
        var queue = new PcmQueue(Format, time);
        queue.Enqueue(Frame(150));
        for (var i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(150 * BytesPerMs, queue.Enqueue(Frame(150)));
        }
        Assert.Equal(30 * 150 * BytesPerMs, queue.DroppedBytes);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
