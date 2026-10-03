namespace Tropicast.Station.Audio.Linux.Tests;

public sealed class PulseSessionTests
{
    [Fact]
    public async Task Short_reads_are_assembled_into_complete_owned_pcm_packets()
    {
        var bytes = new byte[48000 / 50 * 8 * 2];
        bytes[0] = 7;
        using var stream = new FragmentedStream(bytes);
        var stops = 0;
        await using var session = new PulseCaptureSession(new(48000, 2), stream, () => stops++, () => Task.FromResult(0), () => { });
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        var first = reader.Current;
        Assert.Equal(960, first.FrameCount);
        Assert.Equal(7, first.Data.Span[0]);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(0, reader.Current.Data.Span[0]);
        Assert.Equal(7, first.Data.Span[0]);
        var error = await Assert.ThrowsAsync<IOException>(async () => await reader.MoveNextAsync());
        Assert.Contains("ended unexpectedly", error.Message, StringComparison.Ordinal);
        Assert.True(stops > 0);
    }

    [Theory]
    [InlineData(3, 0, "incomplete")]
    [InlineData(0, 1, "permissions")]
    [InlineData(0, 0, "ended unexpectedly")]
    public async Task Partial_packets_or_process_failure_are_explicit(int bytes, int exitCode, string message)
    {
        using var stream = new MemoryStream(new byte[bytes]);
        await using var session = new PulseCaptureSession(new(48000, 2), stream, () => { }, () => Task.FromResult(exitCode), () => { });
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<IOException>(async () => await reader.MoveNextAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_overrun_is_reported_without_silent_dropping()
    {
        using var stream = new MemoryStream(new byte[48000 / 50 * 8 * 101]);
        await using var session = new PulseCaptureSession(new(48000, 2), stream, () => { }, () => Task.FromResult(0), () => { });
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var count = 0;
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (await reader.MoveNextAsync())
            {
                count++;
            }
        });
        Assert.Equal(100, count);
        Assert.Contains("overrun", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_cancellation_and_repeated_disposal_release_child_and_unblock_reader()
    {
        using var stream = new WaitingStream();
        var releases = 0;
        var session = new PulseCaptureSession(new(48000, 2), stream, () => { }, () => Task.FromResult(0), () => releases++);
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var pending = reader.MoveNextAsync().AsTask();
        await session.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task Consumer_cancellation_stops_capture_and_single_reader_is_enforced()
    {
        using var stream = new WaitingStream();
        var stops = 0;
        await using var session = new PulseCaptureSession(new(48000, 2), stream, () => Interlocked.Increment(ref stops),
            () => Task.FromResult(0), () => { });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var reader = session.ReadFramesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(stops > 0);
        await using var second = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
    }

    [Fact]
    public async Task Concurrent_stop_and_disposal_complete_pending_read_and_release_once()
    {
        using var stream = new WaitingStream();
        var releases = 0;
        var session = new PulseCaptureSession(new(48000, 2), stream, () => { }, () => Task.FromResult(0),
            () => Interlocked.Increment(ref releases));
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var pending = reader.MoveNextAsync().AsTask();
        await Task.WhenAll(
            Task.Run(() => session.StopAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(async () => await session.DisposeAsync(), TestContext.Current.CancellationToken),
            Task.Run(async () => await session.DisposeAsync(), TestContext.Current.CancellationToken));
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, releases);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(37, buffer.Length)], cancellationToken);
    }

    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
