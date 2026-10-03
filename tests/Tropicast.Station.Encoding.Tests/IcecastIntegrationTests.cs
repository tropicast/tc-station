using System.Net;
using Tropicast.Station.Core.Broadcasting;

namespace Tropicast.Station.Encoding.Tests;

public sealed class IcecastIntegrationTests
{
    [Fact]
    public async Task Real_Icecast_listener_receives_audio_mpeg_and_decodable_tone()
    {
        if (Environment.GetEnvironmentVariable("TC_TEST_ICECAST_PORT") is not { } port)
        {
            Assert.Skip("Set TC_TEST_ICECAST_PORT to Icecast with test source password tc-test-source.");
            return;
        }
        EncoderTests.RequireBundle();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var target = new BroadcastTarget(new(Guid.NewGuid(), "POC", "127.0.0.1",
            int.Parse(port, System.Globalization.CultureInfo.InvariantCulture), $"/encode-{Guid.NewGuid():N}.mp3"), "tc-test-source");
        using var encoder = EncoderTests.CreateEncoder();
        await using var session = await encoder.StartAsync(target, cancellationToken: deadline.Token);
        var feed = EncoderTests.FeedToneAsync(session, 8);
        while (session.Snapshot.State == EncoderState.Starting)
        {
            await Task.Delay(20, deadline.Token);
        }
        Assert.Equal(EncoderState.Streaming, session.Snapshot.State);
        using var http = new HttpClient();
        using var response = await http.GetAsync(target.Profile.Endpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        await using var listener = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var captured = new MemoryStream();
        var bytes = new byte[8192];
        while (captured.Length < 128000 / 8 * 4)
        {
            var count = await listener.ReadAsync(bytes, deadline.Token);
            Assert.True(count > 0);
            captured.Write(bytes, 0, count);
        }
        await feed;
        await session.StopAsync(deadline.Token);
        Assert.Equal(EncoderState.Stopped, session.Snapshot.State);
        await EncoderTests.AssertDecodableAsync(captured.ToArray(), 3.8);
    }
}
