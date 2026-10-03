using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;

namespace Tropicast.Station.Encoding.Tests;

public sealed class BroadcastControllerTests
{
    [Fact]
    public async Task Profile_source_start_live_stop_owns_both_capture_and_encoder()
    {
        var tone = new ToneAudioCaptureProvider();
        await using var capture = new AudioCaptureService(tone, NullLogger<AudioCaptureService>.Instance);
        var encoder = new MemoryEncoder();
        using var controller = Create(capture, encoder);
        var states = new List<BroadcastState>();
        controller.Changed += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Snapshot.State);
            }
        };
        await controller.StartAsync(Guid.NewGuid(), (await tone.GetDevicesAsync(TestContext.Current.CancellationToken))[0].Id,
            cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        Assert.True(encoder.Session.Frames > 0);
        Assert.True(capture.Snapshot.IsCapturing);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.True(controller.Snapshot.Elapsed > TimeSpan.Zero);
        await controller.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(capture.Snapshot.IsCapturing);
        Assert.Equal(BroadcastState.Idle, controller.Snapshot.State);
        Assert.Equal(1, encoder.Session.Disposals);
        Assert.Contains(BroadcastState.Connecting, states);
        Assert.Contains(BroadcastState.Stopping, states);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Device_removal_or_encoder_failure_stops_both_and_shows_error(bool deviceRemoval)
    {
        var tone = new ToneAudioCaptureProvider();
        await using var capture = new AudioCaptureService(tone, NullLogger<AudioCaptureService>.Instance);
        var encoder = new MemoryEncoder();
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), (await tone.GetDevicesAsync(TestContext.Current.CancellationToken))[0].Id,
            cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        if (deviceRemoval)
        {
            tone.SetDevices([]);
        }
        else
        {
            encoder.Session.Fail();
        }
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Error);
        Assert.False(capture.Snapshot.IsCapturing);
        Assert.False(controller.Snapshot.IsActive);
        Assert.Equal(1, encoder.Session.Disposals);
        Assert.Contains(deviceRemoval ? "disconnect" : "lost", controller.Snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_during_connect_cancels_without_starting_capture()
    {
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        var targets = new WaitingTargets();
        using var controller = new BroadcastController(capture, targets, new MemoryEncoder(), NullLogger<BroadcastController>.Instance);
        var start = controller.StartAsync(Guid.NewGuid(), "unused", cancellationToken: TestContext.Current.CancellationToken);
        await targets.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Connecting, controller.Snapshot.State);
        await controller.StopAsync(TestContext.Current.CancellationToken);
        await start;
        Assert.Equal(BroadcastState.Idle, controller.Snapshot.State);
        Assert.False(capture.Snapshot.IsCapturing);
    }

    [Fact]
    public async Task Failed_capture_start_releases_reserved_encoder_and_error_is_retryable()
    {
        var tone = new ToneAudioCaptureProvider();
        await using var capture = new AudioCaptureService(tone, NullLogger<AudioCaptureService>.Instance);
        var encoder = new MemoryEncoder();
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), "missing", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Error, controller.Snapshot.State);
        Assert.Equal(1, encoder.Session.Disposals);
        await controller.StartAsync(Guid.NewGuid(), (await tone.GetDevicesAsync(TestContext.Current.CancellationToken))[0].Id,
            cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        controller.Dispose();
        Assert.False(capture.Snapshot.IsCapturing);
        Assert.Equal(1, encoder.Session.Disposals);
    }

    [Fact]
    public async Task Callback_submit_failure_is_observed_without_throwing_into_capture_thread()
    {
        var tone = new ToneAudioCaptureProvider();
        await using var capture = new AudioCaptureService(tone, NullLogger<AudioCaptureService>.Instance);
        var encoder = new MemoryEncoder { RejectFrames = true };
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), (await tone.GetDevicesAsync(TestContext.Current.CancellationToken))[0].Id,
            cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Error);
        Assert.Contains("queue", controller.Snapshot.Message, StringComparison.Ordinal);
        Assert.False(capture.Snapshot.IsCapturing);
    }

    internal static BroadcastController Create(AudioCaptureService capture, IBroadcastEncoder encoder)
        => new(capture, new FixedTargets(), encoder, NullLogger<BroadcastController>.Instance);

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class WaitingTargets : IBroadcastTargetProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}

internal sealed class FixedTargets : IBroadcastTargetProvider
{
    public Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
        => Task.FromResult(new BroadcastTarget(new(profileId, "Station", "127.0.0.1", 8000, "/test.mp3"), "test-only"));
}

internal sealed class MemoryEncoder : IBroadcastEncoder
{
    internal MemoryEncoderSession Session { get; private set; } = new();
    internal bool RejectFrames { get; init; }
    public Task<IEncoderSession> StartAsync(BroadcastTarget target, EncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        Session = new() { RejectFrames = RejectFrames };
        return Task.FromResult<IEncoderSession>(Session);
    }
}

internal sealed class MemoryEncoderSession : IEncoderSession
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EncoderSnapshot _snapshot = new(EncoderState.Starting, "Starting", 0);
    internal bool RejectFrames { get; init; }
    internal int Frames { get; private set; }
    internal int Disposals { get; private set; }
    public EncoderSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public Task Completion => _completion.Task;
    public void Submit(PcmFrame frame)
    {
        if (Completion.IsCompleted)
        {
            throw new IOException(Snapshot.Message);
        }
        if (RejectFrames)
        {
            throw new IOException("Encoder queue overrun.");
        }
        Frames++;
        Volatile.Write(ref _snapshot, new(EncoderState.Streaming, "Live", frame.Data.Length));
    }
    internal void Fail()
    {
        Volatile.Write(ref _snapshot, new(EncoderState.Failed, "Connection lost.", 0));
        _completion.TrySetException(new IOException("Connection lost."));
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _completion.TrySetResult();
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        Disposals++;
        await StopAsync(TestContext.Current.CancellationToken);
        await Completion;
    }
}
