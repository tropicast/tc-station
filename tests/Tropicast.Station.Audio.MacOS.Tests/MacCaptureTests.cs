namespace Tropicast.Station.Audio.MacOS.Tests;

public sealed class MacCaptureTests
{
    [Fact]
    public async Task Callback_memory_is_owned_and_format_preserved()
    {
        var source = new FakeSource();
        await using var session = new MacCaptureSession(source);
        session.Start();
        var bytes = new byte[16];
        bytes[0] = 42;
        source.Emit(bytes);
        Array.Clear(bytes);
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(42, reader.Current.Data.Span[0]);
        Assert.Equal(source.Format, reader.Current.Format);
        Assert.Equal(2, reader.Current.FrameCount);
        await session.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(await reader.MoveNextAsync());
    }

    [Theory]
    [InlineData(3, "incomplete")]
    [InlineData(48000 * 8 * 2 + 8, "two seconds")]
    public async Task Malformed_packets_and_byte_overruns_fail_and_release_source(int size, string message)
    {
        var source = new FakeSource();
        await using var session = new MacCaptureSession(source);
        session.Start();
        source.Emit(new byte[size]);
        var error = await ReadFailureAsync(session);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task Packet_overrun_is_explicit_and_does_not_silently_drop_frames()
    {
        var source = new FakeSource();
        await using var session = new MacCaptureSession(source);
        session.Start();
        for (var i = 0; i < 257; i++)
        {
            source.Emit(new byte[8]);
        }
        var count = 0;
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var frame in session.ReadFramesAsync(TestContext.Current.CancellationToken))
            {
                Assert.Equal(1, frame.FrameCount);
                count++;
            }
        });
        Assert.Equal(256, count);
        Assert.Contains("queue is full", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "Microphone")]
    [InlineData(2, "macOS 13")]
    [InlineData(3, "Recording")]
    [InlineData(4, "disconnected")]
    [InlineData(5, "format")]
    [InlineData(6, ".app")]
    [InlineData(7, "display")]
    [InlineData(8, "service")]
    public async Task Native_failure_codes_surface_guidance(int code, string message)
    {
        var source = new FakeSource();
        await using var session = new MacCaptureSession(source);
        session.Start();
        source.Fail(MacAudioErrors.Describe(code));
        var error = await ReadFailureAsync(session);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_failure_releases_source_exactly_once()
    {
        var source = new FakeSource { StartError = MacAudioErrors.Describe(1) };
        await using var session = new MacCaptureSession(source);
        Assert.Throws<IOException>(session.Start);
        await session.DisposeAsync();
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task Concurrent_stop_disposal_and_late_callbacks_unblock_pending_read()
    {
        var source = new FakeSource();
        var session = new MacCaptureSession(source);
        session.Start();
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var pending = reader.MoveNextAsync().AsTask();
        await Task.WhenAll(session.StopAsync(TestContext.Current.CancellationToken),
            session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        source.Emit(new byte[16]);
        source.Fail(new IOException("late callback"));
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task Cancellation_releases_source_and_second_reader_is_rejected()
    {
        var source = new FakeSource();
        await using var session = new MacCaptureSession(source);
        session.Start();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var reader = session.ReadFramesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, source.Disposals);
        await using var second = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
    }

    [Fact]
    public async Task Native_cleanup_errors_are_not_hidden()
    {
        var source = new FakeSource { DisposeError = new IOException("cleanup failed") };
        var session = new MacCaptureSession(source);
        session.Start();
        var error = await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
        Assert.Equal("cleanup failed", error.Message);
    }

    private static async Task<IOException> ReadFailureAsync(MacCaptureSession session)
        => await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in session.ReadFramesAsync(TestContext.Current.CancellationToken)) { }
        });
}

internal sealed class FakeSource : IMacAudioSource
{
    public AudioFormat Format { get; } = new(48000, 2);
    public event EventHandler<MacPcmEventArgs>? DataAvailable;
    public event EventHandler<MacFailureEventArgs>? Failed;
    internal IOException? StartError { get; init; }
    internal IOException? DisposeError { get; init; }
    internal int Disposals { get; private set; }
    public void Start()
    {
        if (StartError is { } error)
        {
            throw error;
        }
    }
    internal void Emit(byte[] bytes) => DataAvailable?.Invoke(this, new(bytes));
    internal void Fail(IOException error) => Failed?.Invoke(this, new(error));
    public void Dispose()
    {
        Disposals++;
        if (DisposeError is { } error)
        {
            throw error;
        }
    }
}
