using System.Runtime.InteropServices;
#pragma warning disable CA2201 // Deliberately simulate errors thrown by COM/WASAPI, not application exceptions.

namespace Tropicast.Station.Audio.Windows.Tests;

public sealed class WasapiSessionTests
{
    [Fact]
    public async Task Callback_buffer_is_copied_and_owned_by_each_frame()
    {
        var source = new FakeSource();
        await using var session = new WasapiCaptureSession(source);
        session.Start();
        var buffer = new byte[16];
        buffer[0] = 7;
        source.Emit(buffer);
        buffer[0] = 99;
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(7, reader.Current.Data.Span[0]);
        Assert.Equal(2, reader.Current.FrameCount);
        Assert.Equal(source.Format, reader.Current.Format);
        source.End();
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Stop_unblocks_pending_reader_and_dispose_is_idempotent()
    {
        var source = new FakeSource();
        var session = new WasapiCaptureSession(source);
        session.Start();
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var pending = reader.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        await session.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await session.StopAsync(TestContext.Current.CancellationToken);
        await session.DisposeAsync();
        await session.DisposeAsync();
        source.Emit(new byte[8]);
        Assert.Equal(1, source.Stops);
        Assert.Equal(1, source.Disposes);
    }

    [Fact]
    public async Task Reader_cancellation_requests_native_stop()
    {
        var source = new FakeSource();
        await using var session = new WasapiCaptureSession(source);
        session.Start();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var reader = session.ReadFramesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, source.Stops);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(17)]
    public async Task Malformed_callback_is_an_explicit_error(int count)
    {
        var source = new FakeSource();
        await using var session = new WasapiCaptureSession(source);
        session.Start();
        source.Emit(new byte[16], count);
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<IOException>(async () => await reader.MoveNextAsync());
        Assert.Contains("malformed", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.Stops);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overrun_by_bytes_or_packet_count_stops_and_never_silently_drops(bool byteBudget)
    {
        var source = new FakeSource();
        await using var session = new WasapiCaptureSession(source);
        session.Start();
        if (byteBudget)
        {
            source.Emit(new byte[source.Format.SampleRate * source.Format.BytesPerFrame * 2 + 8]);
        }
        else
        {
            for (var i = 0; i < 257; i++)
            {
                source.Emit(new byte[8]);
            }
        }

        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (await reader.MoveNextAsync()) { }
        });
        Assert.Contains("overrun", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.Stops);
    }

    [Fact]
    public async Task Native_disconnect_maps_to_actionable_error_and_single_reader_is_enforced()
    {
        var source = new FakeSource();
        await using var session = new WasapiCaptureSession(source);
        session.Start();
        source.End(new COMException("Native diagnostic", unchecked((int)0x88890004)));
        await using var reader = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<IOException>(async () => await reader.MoveNextAsync());
        Assert.Contains("disconnected", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Native diagnostic", error.Message, StringComparison.Ordinal);
        await using var second = session.ReadFramesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
    }

    [Fact]
    public void Failed_native_start_releases_capture()
    {
        var source = new FakeSource { StartError = new COMException("Access denied", unchecked((int)0x80070005)) };
        var session = new WasapiCaptureSession(source);
        Assert.Throws<COMException>(session.Start);
        Assert.Equal(1, source.Disposes);
    }
}
