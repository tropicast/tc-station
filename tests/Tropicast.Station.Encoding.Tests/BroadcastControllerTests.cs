using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Infrastructure;
using Tropicast.Station.Core.Profiles;

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
            encoder.Session.Fail(new EncoderException("Permanent encoder failure: lost the configured encoder."));
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
    public async Task Failed_capture_start_does_not_reserve_encoder_and_manual_retry_can_start()
    {
        var tone = new ToneAudioCaptureProvider();
        await using var capture = new AudioCaptureService(tone, NullLogger<AudioCaptureService>.Instance);
        var encoder = new MemoryEncoder();
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), "missing", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Error, controller.Snapshot.State);
        Assert.Equal(0, encoder.Session.Disposals);
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

    [Fact]
    public async Task Transient_loss_keeps_capture_and_meters_recovers_and_counts_downtime()
    {
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        var encoder = new RetryingEncoder();
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        encoder.Session.Fail();
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Reconnecting);
        Assert.True(capture.Snapshot.IsCapturing);
        Assert.True(capture.Levels.Read().IsActive);
        Assert.Equal(1, controller.Snapshot.RetryAttempt);
        Assert.InRange(controller.Snapshot.RetryIn.TotalSeconds, 0.5, 1.2);
        var elapsed = controller.Snapshot.Elapsed;
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        Assert.Equal(2, encoder.Starts);
        Assert.Equal(1, controller.Snapshot.ReconnectCount);
        Assert.True(controller.Snapshot.Downtime >= TimeSpan.FromMilliseconds(700));
        Assert.True(controller.Snapshot.Elapsed < elapsed + TimeSpan.FromSeconds(1));
        Assert.Equal(0, controller.Snapshot.RetryAttempt);
        Assert.False(controller.Snapshot.IsRetryConnecting);
        encoder.Session.Fail();
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Reconnecting);
        Assert.Equal(1, controller.Snapshot.RetryAttempt); // Recovery resets consecutive backoff.
        await controller.RetryNowAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        Assert.Equal(2, controller.Snapshot.ReconnectCount);
        await controller.StopAsync(TestContext.Current.CancellationToken);
        var downtime = controller.Snapshot.Downtime;
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(downtime, controller.Snapshot.Downtime);
        await controller.StartAsync(Guid.NewGuid(), "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, controller.Snapshot.ReconnectCount);
        Assert.Equal(TimeSpan.Zero, controller.Snapshot.Downtime);
    }

    [Fact]
    public async Task Capture_and_reconnect_use_the_saved_profile_quality_and_metadata()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "Saved settings", "127.0.0.1", 8000, "/settings.mp3",
            BitrateKbps: 320, SampleRate: 48000, Channels: 1, StreamName: "Saved station");
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        var encoder = new RetryingEncoder();
        using var controller = new BroadcastController(capture, new FixedTargets(profile), encoder,
            NullLogger<BroadcastController>.Instance);
        await controller.StartAsync(profile.Id, "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        encoder.Session.Fail();
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Reconnecting);
        await controller.RetryNowAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        Assert.Equal(2, encoder.Requests.Count);
        Assert.All(encoder.Requests, request =>
        {
            Assert.Equal(profile, request.Target.Profile);
            Assert.Equal(320, request.Options.BitrateKbps);
            Assert.Equal(new AudioFormat(48000, 1), request.Options.Format);
        });
        Assert.Equal(new AudioFormat(48000, 1), encoder.Session.LastFormat);
    }

    [Fact]
    public async Task Initial_unreachable_server_retries_with_capture_but_auth_on_retry_is_terminal()
    {
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        var encoder = new RetryingEncoder
        {
            StartError = attempt => new IcecastSourceException(attempt == 1
                ? ConnectionTestStatus.Unreachable : ConnectionTestStatus.AuthenticationFailed,
                attempt == 1 ? "Server unavailable." : "Authentication failed. Check the source password."),
        };
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Reconnecting, controller.Snapshot.State);
        Assert.True(capture.Snapshot.IsCapturing);
        await controller.RetryNowAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Error);
        Assert.Equal(2, encoder.Starts);
        Assert.Contains("Authentication", controller.Snapshot.Message, StringComparison.Ordinal);
        Assert.False(capture.Snapshot.IsCapturing);
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Assert.Equal(2, encoder.Starts);
    }

    [Fact]
    public async Task Stop_during_backoff_prevents_retry_and_device_loss_is_terminal()
    {
        var tone = new ToneAudioCaptureProvider();
        await using var capture = new AudioCaptureService(tone, NullLogger<AudioCaptureService>.Instance);
        var encoder = new RetryingEncoder { StartError = _ => new IOException("Network unavailable.") };
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Reconnecting, controller.Snapshot.State);
        await controller.StopAsync(TestContext.Current.CancellationToken);
        await Task.Delay(1300, TestContext.Current.CancellationToken);
        Assert.Equal(1, encoder.Starts);
        Assert.False(capture.Snapshot.IsCapturing);
        await controller.StartAsync(Guid.NewGuid(), "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        tone.SetDevices([]);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Error);
        Assert.Equal(2, encoder.Starts);
        Assert.Contains("disconnect", controller.Snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_cancels_an_inflight_reconnect_handshake_without_deadlock()
    {
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        var encoder = new RetryingEncoder { BlockRetry = true };
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        encoder.Session.Fail();
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Reconnecting);
        await controller.RetryNowAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => controller.Snapshot.IsRetryConnecting);
        await controller.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Idle, controller.Snapshot.State);
        Assert.False(capture.Snapshot.IsCapturing);
    }

    [Fact]
    public async Task Repeated_transient_failures_increase_attempts_and_retry_now_skips_wait()
    {
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        var encoder = new RetryingEncoder { StartError = _ => new IOException("Network unavailable.") };
        using var controller = Create(capture, encoder);
        await controller.StartAsync(Guid.NewGuid(), "demo-input", cancellationToken: TestContext.Current.CancellationToken);
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await UntilAsync(() => controller.Snapshot.RetryAttempt == attempt);
            var nominal = Math.Min(30, Math.Pow(2, attempt - 1));
            Assert.InRange(controller.Snapshot.RetryIn.TotalSeconds, nominal * 0.8 - 0.3, Math.Min(30, nominal * 1.2));
            await controller.RetryNowAsync(TestContext.Current.CancellationToken);
        }
        await UntilAsync(() => encoder.Starts == 7);
        Assert.True(capture.Levels.Read().IsActive);
    }

    [Theory]
    [InlineData(ConnectionTestStatus.AuthenticationFailed, false)]
    [InlineData(ConnectionTestStatus.TlsFailed, false)]
    [InlineData(ConnectionTestStatus.MountInUse, false)]
    [InlineData(ConnectionTestStatus.Rejected, false)]
    [InlineData(ConnectionTestStatus.Unreachable, true)]
    [InlineData(ConnectionTestStatus.TimedOut, true)]
    public void Typed_source_status_controls_retry_not_message_text(ConnectionTestStatus status, bool retry)
        => Assert.Equal(retry, BroadcastController.CanRetry(new IcecastSourceException(status, "arbitrary redacted message")));

    [Fact]
    public void Backoff_doubles_has_jitter_and_caps_at_thirty_seconds()
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var nominal = Math.Min(30, Math.Pow(2, attempt - 1));
            Assert.Equal(nominal * 0.8, BroadcastController.Backoff(attempt, 0).TotalSeconds, precision: 6);
            Assert.Equal(nominal, BroadcastController.Backoff(attempt, 0.5).TotalSeconds, precision: 6);
            Assert.Equal(Math.Min(30, nominal * 1.2), BroadcastController.Backoff(attempt, 1).TotalSeconds, precision: 6);
        }
        Assert.False(BroadcastController.CanRetry(new EncoderException("Encoder queue overrun.")));
        Assert.True(BroadcastController.CanRetry(new IOException("Unexpected child exit.")));
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

    internal sealed class RetryingEncoder : IBroadcastEncoder
    {
        internal List<(BroadcastTarget Target, EncoderOptions Options)> Requests { get; } = [];
        internal int Starts { get; private set; }
        internal MemoryEncoderSession Session { get; private set; } = new();
        internal Func<int, IOException?>? StartError { get; init; }
        internal bool BlockRetry { get; init; }

        public async Task<IEncoderSession> StartAsync(BroadcastTarget target, EncoderOptions? options = null, CancellationToken cancellationToken = default)
        {
            Starts++;
            Requests.Add((target, options!));
            if (BlockRetry && Starts > 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (StartError?.Invoke(Starts) is { } error)
            {
                throw error;
            }
            Session = new();
            return Session;
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

internal sealed class FixedTargets(ConnectionProfile? profile = null) : IBroadcastTargetProvider
{
    public Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
        => Task.FromResult(new BroadcastTarget(profile ?? new(profileId, "Station", "127.0.0.1", 8000, "/test.mp3"), "test-only"));
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
    internal AudioFormat? LastFormat { get; private set; }
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
            throw new EncoderException("Encoder queue overrun.");
        }
        Frames++;
        LastFormat = frame.Format;
        Volatile.Write(ref _snapshot, new(EncoderState.Streaming, "Live", frame.Data.Length));
    }
    internal void Fail(IOException? error = null)
    {
        error ??= new IOException("Connection lost.");
        Volatile.Write(ref _snapshot, new(EncoderState.Failed, error.Message, 0));
        _completion.TrySetException(error);
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
