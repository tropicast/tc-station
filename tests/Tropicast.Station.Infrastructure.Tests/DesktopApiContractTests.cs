using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tropicast.Station.Core.Account;
using Tropicast.Station.Tests;

namespace Tropicast.Station.Infrastructure.Tests;

/// <summary>
/// The client against a real tc-dashboard (its <c>compose.yaml</c>): set <c>TC_DASHBOARD_URL=http://localhost:8080</c>
/// and <c>TC_DASHBOARD_MAIL_URL=http://localhost:8025</c> (Mailpit). Skipped otherwise. The browser part needs HTTPS
/// (its cookies are Secure): <c>TC_DASHBOARD_WEB_URL</c>, e.g. <c>https://localhost:8443</c> with a self-signed
/// certificate, accepted for loopback addresses only.
/// </summary>
public sealed partial class DesktopApiContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Device_sign_in_stations_and_broadcast_target_work_against_tc_dashboard()
    {
        var dashboard = Environment.GetEnvironmentVariable("TC_DASHBOARD_URL");
        var mail = Environment.GetEnvironmentVariable("TC_DASHBOARD_MAIL_URL");
        if (dashboard is null || mail is null)
        {
            Assert.Skip("Set TC_DASHBOARD_URL and TC_DASHBOARD_MAIL_URL to run against tc-dashboard compose.yaml.");
        }
        using var web = new WebUser(new Uri(Environment.GetEnvironmentVariable("TC_DASHBOARD_WEB_URL") ?? dashboard), new Uri(mail));
        await web.SignUpAndLoginAsync();
        var stationId = await web.CreateStationAsync();

        using var api = new HttpDesktopApi(new Uri(dashboard));
        var secrets = new MemorySecrets();
        using var account = new AccountService(api, new MemoryAccountStore(), secrets, TimeProvider.System);
        await account.SignInAsync("Contract test", request => web.ApproveAsync(request.UserCode), Token);

        var station = Assert.Single(account.Stations);
        Assert.Equal(stationId, station.StationId);
        var issued = await account.GetTargetAsync(stationId, renew: false, Token);
        var profile = AccountBroadcastTargetProvider.ToProfile(issued.Target);
        Assert.Equal(station.PublicId, profile.Username);
        Assert.Equal($"/stations/{station.PublicId}/live.mp3", profile.Mount);
        Assert.True(profile.UseTls);
        Assert.True(profile.PublishOpus);
        Assert.Equal(64, profile.BitrateKbps);

        var renewed = await account.GetTargetAsync(stationId, renew: true, Token);
        Assert.NotEqual(issued.Password, renewed.Password);
        var credentials = await web.CredentialsAsync(stationId);
        Assert.Equal(1, credentials.Count(c => c.GetProperty("revokedAt").ValueKind == JsonValueKind.Null));

        await account.SignOutAsync(Token);
        Assert.Empty(secrets.Items);
        credentials = await web.CredentialsAsync(stationId);
        Assert.All(credentials, c => Assert.NotEqual(JsonValueKind.Null, c.GetProperty("revokedAt").ValueKind));
    }

    /// <summary>The user in the browser: cookie session and antiforgery header, handled by hand (the cookie is Secure).</summary>
    private sealed partial class WebUser(Uri dashboard, Uri mail) : IDisposable
    {
        private const string Password = "contract test passphrase";
        private readonly HttpClient _http = new(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || dashboard.IsLoopback,
        })
        { BaseAddress = dashboard };
        private readonly HttpClient _mail = new() { BaseAddress = mail };
        private readonly Dictionary<string, string> _cookies = [];
        private readonly string _email = $"contract-{Guid.NewGuid():N}@example.test";
        private string? _xsrf;
        private Guid _tenantId;

        public async Task SignUpAndLoginAsync()
        {
            await SendAsync(HttpMethod.Post, "/api/v1/auth/register", new { email = _email, password = Password });
            var search = await _mail.GetFromJsonAsync<JsonElement>($"/api/v1/search?query=to:{_email}", Token);
            var id = search.GetProperty("messages")[0].GetProperty("ID").GetString();
            var text = (await _mail.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}", Token)).GetProperty("Text").GetString()!;
            var link = new Uri(ConfirmLink().Match(text).Value);
            var query = System.Web.HttpUtility.ParseQueryString(link.Query);
            await SendAsync(HttpMethod.Post, "/api/v1/auth/confirm-email", new { userId = query["userId"], code = query["code"] });
            await RefreshAntiforgeryAsync();
            await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = _email, password = Password });
            await RefreshAntiforgeryAsync();
        }

        public async Task<Guid> CreateStationAsync()
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            _tenantId = (await SendAsync(HttpMethod.Post, "/api/v1/tenants", new { name = $"Contract {suffix}", slug = $"c-{suffix}" }))
                .GetProperty("id").GetGuid();
            return (await SendAsync(HttpMethod.Post, "/api/v1/stations", new { name = "Contract radio", slug = "contract" }))
                .GetProperty("id").GetGuid();
        }

        public async Task ApproveAsync(string userCode) => await SendAsync(HttpMethod.Post, "/api/v1/auth/device/approve", new { userCode });

        public async Task<List<JsonElement>> CredentialsAsync(Guid stationId)
            => [.. (await SendAsync(HttpMethod.Get, $"/api/v1/stations/{stationId}/credentials", null)).EnumerateArray()];

        private async Task RefreshAntiforgeryAsync()
        {
            await SendAsync(HttpMethod.Get, "/api/v1/auth/antiforgery", null);
            _xsrf = _cookies["XSRF-TOKEN"];
        }

        private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }
            if (_xsrf is not null)
            {
                request.Headers.Add("X-XSRF-TOKEN", _xsrf);
            }
            if (_tenantId != Guid.Empty)
            {
                request.Headers.Add("X-Tenant-Id", _tenantId.ToString());
            }
            using var response = await _http.SendAsync(request, Token);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies.Select(c => c.Split(';')[0].Split('=', 2)))
                {
                    _cookies[cookie[0]] = cookie[1];
                }
            }
            var content = await response.Content.ReadAsStringAsync(Token);
            Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {content}");
            return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement.Clone();
        }

        public void Dispose()
        {
            _http.Dispose();
            _mail.Dispose();
        }

        [GeneratedRegex(@"http[^\s]*confirm-email[^\s]*")]
        private static partial Regex ConfirmLink();
    }
}
