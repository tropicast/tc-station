using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Encoding;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.E2E.Tests;

/// <summary>
/// Full pipeline against the real Tropicast Icecast image: synthetic capture, FFmpeg MP3 encoder,
/// source handshake and a listener that decodes the result.
/// </summary>
public sealed class PipelineE2ETests
{
    private const string DemoInput = "demo-input";

    [Fact]
    public async Task Tone_goes_live_on_the_mount_and_a_listener_decodes_it()
    {
        E2EEnvironment.Require(needsContainer: true);
        using var deadline = Deadline(60);
        await using var server = await TropicastContainer.StartAsync(deadline.Token);
        var profile = Profile(server.Port);
        await using var station = new Rig(profile, server.SourcePassword);

        await station.Controller.StartAsync(profile.Id, DemoInput, cancellationToken: deadline.Token);
        await Eventually.Async(() => station.Controller.Snapshot.State == BroadcastState.Live,
            "the broadcast to go Live", station.Describe, deadline.Token);
        await Eventually.Async(async () => (await server.ActiveMountsAsync(deadline.Token)).Contains(profile.Mount),
            $"{profile.Mount} to be an active source on Tropicast", station.Describe, deadline.Token);

        await Media.AssertListenerHearsAudioAsync(profile, 2, deadline.Token);

        await station.Controller.StopAsync(deadline.Token);
        Assert.Equal(BroadcastState.Idle, station.Controller.Snapshot.State);
        await Eventually.Async(async () => !(await server.ActiveMountsAsync(deadline.Token)).Contains(profile.Mount),
            $"{profile.Mount} to be released after Stop", station.Describe, deadline.Token);
    }

    [Fact]
    public async Task Restarting_the_server_while_live_recovers_without_user_action()
    {
        E2EEnvironment.Require(needsContainer: true);
        using var deadline = Deadline(120);
        await using var server = await TropicastContainer.StartAsync(deadline.Token);
        var profile = Profile(server.Port);
        await using var station = new Rig(profile, server.SourcePassword);

        await station.Controller.StartAsync(profile.Id, DemoInput, cancellationToken: deadline.Token);
        await Eventually.Async(() => station.Controller.Snapshot.State == BroadcastState.Live,
            "the broadcast to go Live", station.Describe, deadline.Token);
        await Media.AssertListenerHearsAudioAsync(profile, 2, deadline.Token);

        await server.RestartAsync(deadline.Token);

        // No Retry press: the controller must notice the drop and reconnect on its own.
        await Eventually.Async(() => station.Controller.Snapshot.ReconnectCount >= 1
                && station.Controller.Snapshot.State == BroadcastState.Live,
            "automatic recovery to Live after the server restart", station.Describe, deadline.Token);
        await Eventually.Async(async () => (await server.ActiveMountsAsync(deadline.Token)).Contains(profile.Mount),
            $"{profile.Mount} to be an active source again", station.Describe, deadline.Token);
        await Media.AssertListenerHearsAudioAsync(profile, 2, deadline.Token);

        await station.Controller.StopAsync(deadline.Token);
    }

    [Fact]
    public async Task Wrong_password_fails_clearly_and_does_not_retry()
    {
        E2EEnvironment.Require(needsContainer: true);
        using var deadline = Deadline(60);
        await using var server = await TropicastContainer.StartAsync(deadline.Token);
        var profile = Profile(server.Port);
        var target = new BroadcastTarget(profile, "not-the-source-password");
        await using var station = new Rig(profile, target.Password);

        var test = await new TropicastConnectionTester().TestAsync(target, deadline.Token);
        Assert.Equal(ConnectionTestStatus.AuthenticationFailed, test.Status);

        await station.Controller.StartAsync(profile.Id, DemoInput, cancellationToken: deadline.Token);
        await Eventually.Async(() => station.Controller.Snapshot.State == BroadcastState.Error,
            "the broadcast to fail with an authentication error", station.Describe, deadline.Token);
        Assert.Contains("Authentication failed", station.Controller.Snapshot.Message, StringComparison.Ordinal);
        Assert.False(station.Capture.Snapshot.IsCapturing);

        await Task.Delay(1500, deadline.Token);
        Assert.Equal(BroadcastState.Error, station.Controller.Snapshot.State);
        Assert.Equal(0, station.Controller.Snapshot.RetryAttempt);
        Assert.DoesNotContain(profile.Mount, await server.ActiveMountsAsync(deadline.Token));
    }

    [Fact]
    public async Task Mount_already_in_use_is_rejected_and_the_first_source_keeps_playing()
    {
        E2EEnvironment.Require(needsContainer: true);
        using var deadline = Deadline(90);
        await using var server = await TropicastContainer.StartAsync(deadline.Token);
        var profile = Profile(server.Port);
        await using var first = new Rig(profile, server.SourcePassword);
        await using var second = new Rig(profile, server.SourcePassword);

        await first.Controller.StartAsync(profile.Id, DemoInput, cancellationToken: deadline.Token);
        await Eventually.Async(() => first.Controller.Snapshot.State == BroadcastState.Live,
            "the first source to go Live", first.Describe, deadline.Token);
        await Eventually.Async(async () => (await server.ActiveMountsAsync(deadline.Token)).Contains(profile.Mount),
            $"{profile.Mount} to be an active source", first.Describe, deadline.Token);

        var test = await new TropicastConnectionTester().TestAsync(new(profile, server.SourcePassword), deadline.Token);
        Assert.Equal(ConnectionTestStatus.MountInUse, test.Status);

        await second.Controller.StartAsync(profile.Id, DemoInput, cancellationToken: deadline.Token);
        await Eventually.Async(() => second.Controller.Snapshot.State == BroadcastState.Error,
            "the second source to be rejected", second.Describe, deadline.Token);
        Assert.Contains("already in use", second.Controller.Snapshot.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, second.Controller.Snapshot.RetryAttempt);

        Assert.Equal(BroadcastState.Live, first.Controller.Snapshot.State);
        await Media.AssertListenerHearsAudioAsync(profile, 2, deadline.Token);
    }

    [Fact]
    public async Task Unreachable_host_reports_clearly_and_keeps_retrying_until_stopped()
    {
        E2EEnvironment.Require(needsContainer: false);
        using var deadline = Deadline(60);
        // A port that was just free and has nothing listening on it.
        var profile = Profile(TropicastContainer.FreePort());
        var target = new BroadcastTarget(profile, "irrelevant");
        await using var station = new Rig(profile, target.Password);

        var test = await new TropicastConnectionTester().TestAsync(target, deadline.Token);
        Assert.Equal(ConnectionTestStatus.Unreachable, test.Status);

        await station.Controller.StartAsync(profile.Id, DemoInput, cancellationToken: deadline.Token);
        await Eventually.Async(() => station.Controller.Snapshot.State == BroadcastState.Reconnecting
                && station.Controller.Snapshot.RetryAttempt >= 1,
            "the controller to report that the server is unreachable and retry", station.Describe, deadline.Token);
        Assert.Contains("reach", station.Controller.Snapshot.Message, StringComparison.OrdinalIgnoreCase);

        await station.Controller.StopAsync(deadline.Token);
        Assert.Equal(BroadcastState.Idle, station.Controller.Snapshot.State);
        Assert.False(station.Capture.Snapshot.IsCapturing);
    }

    private static CancellationTokenSource Deadline(int seconds)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }

    private static ConnectionProfile Profile(int port) => new(Guid.NewGuid(), "E2E", "127.0.0.1", port,
        $"/e2e-{Guid.NewGuid():N}.mp3", BitrateKbps: 128, SampleRate: 48000, Channels: 1);

    /// <summary>One rig: tone capture, encoder and controller wired like the app does.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly FfmpegBroadcastEncoder _encoder = new(new FfmpegExecutable(), NullLogger<FfmpegBroadcastEncoder>.Instance);

        internal Rig(ConnectionProfile profile, string sourcePassword)
        {
            Capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
            Controller = new BroadcastController(Capture, new Target(new(profile, sourcePassword)),
                _encoder, NullLogger<BroadcastController>.Instance);
        }

        internal AudioCaptureService Capture { get; }
        internal BroadcastController Controller { get; }

        internal string Describe()
            => $"state={Controller.Snapshot.State}, retries={Controller.Snapshot.RetryAttempt}, message='{Controller.Snapshot.Message}'";

        public async ValueTask DisposeAsync()
        {
            await Controller.StopAsync(CancellationToken.None);
            Controller.Dispose();
            _encoder.Dispose();
            await Capture.DisposeAsync();
        }
    }

    private sealed class Target(BroadcastTarget target) : IBroadcastTargetProvider
    {
        public Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
            => Task.FromResult(target);
    }
}

internal static class Eventually
{
    /// <summary>Polls until true; a timeout fails with what was awaited and the pipeline's last state.</summary>
    internal static Task Async(Func<bool> condition, string expectation, Func<string> state, CancellationToken token)
        => Async(() => Task.FromResult(condition()), expectation, state, token);

    internal static async Task Async(Func<Task<bool>> condition, string expectation, Func<string> state, CancellationToken token)
    {
        try
        {
            while (!await condition())
            {
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for {expectation}. Last state: {state()}");
        }
    }
}
