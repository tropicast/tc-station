using System.Net;
using System.Text;
using Tropicast.Station.Core.Account;

namespace Tropicast.Station.Infrastructure.Tests;

/// <summary>The client against canned answers in the shape of tc-dashboard docs/desktop-api.md.</summary>
public sealed class HttpDesktopApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, status == HttpStatusCode.OK ? "application/json" : "application/problem+json") };

    private static (HttpDesktopApi Api, CannedHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new CannedHandler(respond);
        return (new HttpDesktopApi(new Uri("https://app.test"), handler), handler);
    }

    [Theory]
    [InlineData("https://app.tropicastradio.com", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://app.tropicastradio.com", false)]
    [InlineData("ftp://app.tropicastradio.com", false)]
    public void Only_https_or_this_computer_is_allowed(string url, bool allowed)
        => Assert.Equal(allowed, HttpDesktopApi.IsAllowed(new Uri(url)));

    [Fact]
    public async Task Device_sign_in_starts_and_maps_every_poll_answer()
    {
        var answers = new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """{"deviceCode":"0192.secret","userCode":"BCDF-GHJK","verificationUri":"https://app.test/device","verificationUriComplete":"https://app.test/device?code=BCDF-GHJK","expiresIn":600,"interval":5}"""),
            Json(HttpStatusCode.BadRequest, """{"title":"Waiting","status":400,"error":"authorization_pending"}"""),
            Json(HttpStatusCode.BadRequest, """{"title":"Too fast","status":400,"error":"slow_down"}"""),
            Json(HttpStatusCode.BadRequest, """{"title":"Refused","status":400,"error":"access_denied"}"""),
            Json(HttpStatusCode.BadRequest, """{"title":"Expired","status":400,"error":"expired_token"}"""),
            Json(HttpStatusCode.OK, """{"tokenType":"Bearer","accessToken":"at","expiresIn":900,"refreshToken":"rt"}"""),
        ]);
        var (api, handler) = Create(_ => answers.Dequeue());
        using var _ = api;

        var start = await api.StartSignInAsync("Studio PC", Token);
        Assert.Equal("BCDF-GHJK", start.UserCode);
        Assert.Equal(TimeSpan.FromSeconds(5), start.Interval);
        Assert.DoesNotContain("secret", start.ToString(), StringComparison.Ordinal);
        Assert.Equal(SignInPollStatus.Pending, (await api.PollSignInAsync(start.DeviceSecret, Token)).Status);
        Assert.Equal(SignInPollStatus.SlowDown, (await api.PollSignInAsync(start.DeviceSecret, Token)).Status);
        Assert.Equal(SignInPollStatus.Denied, (await api.PollSignInAsync(start.DeviceSecret, Token)).Status);
        Assert.Equal(SignInPollStatus.Expired, (await api.PollSignInAsync(start.DeviceSecret, Token)).Status);
        var approved = await api.PollSignInAsync(start.DeviceSecret, Token);
        Assert.Equal(SignInPollStatus.Approved, approved.Status);
        Assert.Equal(("at", "rt", TimeSpan.FromMinutes(15)), (approved.Tokens!.AccessToken, approved.Tokens.RefreshToken, approved.Tokens.ExpiresIn));

        Assert.Equal("/api/v1/auth/device/code", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"deviceName":"Studio PC"}""", handler.Requests[0].Body);
        Assert.Equal("""{"deviceCode":"0192.secret"}""", handler.Requests[1].Body);
        Assert.All(handler.Requests, r => Assert.Null(r.Request.Headers.Authorization));
    }

    [Fact]
    public async Task Stations_and_the_broadcast_target_use_the_bearer_token_and_tenant()
    {
        var tenant = Guid.NewGuid();
        var station = Guid.NewGuid();
        var (api, handler) = Create(request => request.RequestUri!.AbsolutePath.EndsWith("/me/stations", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, $$"""[{"tenantId":"{{tenant}}","tenantName":"My radios","stationId":"{{station}}","publicId":"k3m9x2p7qa","name":"Radio Mada","role":"Owner"}]""")
            : Json(HttpStatusCode.OK, $$"""
                {"stationId":"{{station}}","publicId":"k3m9x2p7qa","stationName":"Radio Mada","credentialId":"{{Guid.NewGuid()}}",
                 "username":"k3m9x2p7qa","password":"pw","maxBitrateKbps":128,"outputs":[
                 {"format":"Mp3","contentType":"audio/mpeg","ingestUrl":"https://ingest.test/stations/k3m9x2p7qa/live.mp3","listenerUrl":"https://listen.test/stations/k3m9x2p7qa/live.mp3"},
                 {"format":"Opus","contentType":"audio/ogg","ingestUrl":"https://ingest.test/stations/k3m9x2p7qa/live.opus","listenerUrl":"https://listen.test/stations/k3m9x2p7qa/live.opus"}]}
                """));
        using var _ = api;

        var stations = await api.GetStationsAsync("at", Token);
        var issued = await api.GetBroadcastTargetAsync("at", tenant, station, Token);

        Assert.Equal(new AccountStation(tenant, "My radios", station, "k3m9x2p7qa", "Radio Mada", "Owner"), Assert.Single(stations));
        Assert.Equal("pw", issued.Password);
        Assert.Equal(2, issued.Target.Outputs.Count);
        Assert.DoesNotContain("pw", issued.ToString(), StringComparison.Ordinal);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer at", r.Request.Headers.Authorization!.ToString()));
        var post = handler.Requests[1].Request;
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal($"/api/v1/stations/{station}/broadcast-target", post.RequestUri!.AbsolutePath);
        Assert.Equal(tenant.ToString(), Assert.Single(post.Headers.GetValues("X-Tenant-Id")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DesktopApiError.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, DesktopApiError.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, DesktopApiError.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, DesktopApiError.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, DesktopApiError.Unavailable)]
    [InlineData(HttpStatusCode.Found, DesktopApiError.Invalid)]
    public async Task Failures_map_to_safe_errors(HttpStatusCode status, DesktopApiError expected)
    {
        var (api, _) = Create(_ => Json(status, """{"title":"Only a signed-in desktop device gets a broadcast target.","status":403}"""));
        using var __ = api;

        var error = await Assert.ThrowsAsync<DesktopApiException>(() => api.RefreshAsync("rt-secret", Token));

        Assert.Equal(expected, error.Error);
        Assert.DoesNotContain("rt-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_failure_is_unavailable_and_an_insecure_ingest_url_is_refused()
    {
        var (offline, _) = Create(_ => throw new HttpRequestException("No route to host"));
        using (offline)
        {
            Assert.Equal(DesktopApiError.Unavailable,
                (await Assert.ThrowsAsync<DesktopApiException>(() => offline.GetStationsAsync("at", Token))).Error);
        }

        var station = Guid.NewGuid();
        var (api, _) = Create(_ => Json(HttpStatusCode.OK, $$"""
            {"stationId":"{{station}}","publicId":"x","stationName":"X","username":"x","password":"pw","maxBitrateKbps":64,"outputs":[
             {"format":"Mp3","contentType":"audio/mpeg","ingestUrl":"http://ingest.test/stations/x/live.mp3","listenerUrl":"https://listen.test/x"}]}
            """));
        using (api)
        {
            Assert.Equal(DesktopApiError.Invalid,
                (await Assert.ThrowsAsync<DesktopApiException>(() => api.GetBroadcastTargetAsync("at", Guid.NewGuid(), station, Token))).Error);
        }
    }

    [Fact]
    public async Task The_account_file_keeps_stations_and_targets_and_survives_corruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tropicast-account-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonAccountStore(directory);
            Assert.Equal(AccountData.Empty, await store.LoadAsync(Token));
            var station = new AccountStation(Guid.NewGuid(), "T", Guid.NewGuid(), "x", "X", "Owner");
            var target = new StationTarget(station.StationId, "x", "X", "x", 64,
                [new StationOutput("Mp3", "audio/mpeg", new Uri("https://ingest.test/x.mp3"), new Uri("https://listen.test/x.mp3"))]);
            await store.SaveAsync(new AccountData([station], [target], station.StationId), Token);

            var loaded = await store.LoadAsync(Token);
            Assert.Equal(station, Assert.Single(loaded.Stations));
            Assert.Equal(station.StationId, loaded.SelectedStationId);
            Assert.Equal(target.Outputs[0], Assert.Single(Assert.Single(loaded.Targets).Outputs));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "account.json")));
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "account.json"), "{ not json", Token);
            Assert.Equal(AccountData.Empty, await store.LoadAsync(Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
