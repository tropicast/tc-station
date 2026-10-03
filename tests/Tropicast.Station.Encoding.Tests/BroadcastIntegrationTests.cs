using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;

namespace Tropicast.Station.Encoding.Tests;

public sealed class BroadcastIntegrationTests
{
    [Fact]
    public async Task Selected_synthetic_capture_goes_live_and_stops_real_Icecast_source()
    {
        if (Environment.GetEnvironmentVariable("TC_TEST_ICECAST_PORT") is not { } port)
        {
            Assert.Skip("Set TC_TEST_ICECAST_PORT to a local Icecast with source password tc-test-source.");
            return;
        }
        EncoderTests.RequireBundle();
        var profile = new Core.Profiles.ConnectionProfile(Guid.NewGuid(), "Workflow POC", "127.0.0.1",
            int.Parse(port, System.Globalization.CultureInfo.InvariantCulture), $"/workflow-{Guid.NewGuid():N}.mp3");
        var tone = new ToneAudioCaptureProvider();
        await using var capture = new AudioCaptureService(tone, NullLogger<AudioCaptureService>.Instance);
        using var encoder = EncoderTests.CreateEncoder();
        using var controller = new BroadcastController(capture, new Target(new(profile, "tc-test-source")),
            encoder, NullLogger<BroadcastController>.Instance);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await controller.StartAsync(profile.Id, (await tone.GetDevicesAsync(deadline.Token))[0].Id, cancellationToken: deadline.Token);
        await BroadcastControllerTests.UntilAsync(() => controller.Snapshot.State == BroadcastState.Live);
        using var http = new HttpClient();
        using var response = await http.GetAsync(profile.Endpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        await using var listener = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var captured = new MemoryStream();
        var buffer = new byte[8192];
        while (captured.Length < 64000)
        {
            var count = await listener.ReadAsync(buffer, deadline.Token);
            Assert.True(count > 0);
            captured.Write(buffer, 0, count);
        }
        Assert.True(controller.Snapshot.Elapsed.TotalSeconds > 2);
        await controller.StopAsync(deadline.Token);
        Assert.Equal(BroadcastState.Idle, controller.Snapshot.State);
        Assert.False(capture.Snapshot.IsCapturing);
        await EncoderTests.AssertDecodableAsync(captured.ToArray(), 3.8);
    }

    private sealed class Target(BroadcastTarget target) : IBroadcastTargetProvider
    {
        public Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(target.Profile.Id, profileId);
            return Task.FromResult(target);
        }
    }
}
