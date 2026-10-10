using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Tropicast.Station.Core.Account;

namespace Tropicast.Station.Infrastructure;

/// <summary>
/// The tc-dashboard desktop API over HTTPS (docs/desktop-api.md, v1). No redirects are followed, so tokens never go to
/// another host; error messages never contain a token.
/// </summary>
public sealed class HttpDesktopApi : IDesktopApi, IDisposable
{
    public static readonly Uri DefaultBaseUrl = new("https://app.tropicastradio.com");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public HttpDesktopApi(Uri baseUrl, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!IsAllowed(baseUrl))
        {
            throw new ArgumentException("The Tropicast API address must use HTTPS (plain HTTP only on this computer).", nameof(baseUrl));
        }
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = baseUrl,
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    /// <summary>HTTPS, or HTTP to this computer for local testing.</summary>
    public static bool IsAllowed(Uri url)
        => url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));

    public async Task<DeviceSignIn> StartSignInAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/auth/device/code", null, new { deviceName }, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var code = await ReadAsync<DeviceCodeDto>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(code.DeviceCode) || string.IsNullOrEmpty(code.UserCode) || !IsWebPage(code.VerificationUri)
            || !IsWebPage(code.VerificationUriComplete) || code.ExpiresIn <= 0 || code.Interval <= 0)
        {
            throw Invalid();
        }
        return new DeviceSignIn(code.DeviceCode, code.UserCode, code.VerificationUri, code.VerificationUriComplete,
            TimeSpan.FromSeconds(code.ExpiresIn), TimeSpan.FromSeconds(code.Interval));
    }

    public async Task<SignInPoll> PollSignInAsync(string deviceSecret, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/auth/device/token", null, new { deviceCode = deviceSecret },
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var problem = await TryReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            return new SignInPoll(problem?.Error switch
            {
                "authorization_pending" => SignInPollStatus.Pending,
                "slow_down" => SignInPollStatus.SlowDown,
                "access_denied" => SignInPollStatus.Denied,
                "expired_token" => SignInPollStatus.Expired,
                _ => throw Invalid(),
            });
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return new SignInPoll(SignInPollStatus.Approved, await ReadTokensAsync(response, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/auth/token/refresh", null, new { refreshToken }, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadTokensAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/auth/token/revoke", null, new { refreshToken }, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountStation>> GetStationsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/me/stations", accessToken, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var stations = await ReadAsync<List<StationDto>>(response, cancellationToken).ConfigureAwait(false);
        if (stations.Any(s => s is null || s.StationId == Guid.Empty || string.IsNullOrEmpty(s.Name) || string.IsNullOrEmpty(s.PublicId)))
        {
            throw Invalid();
        }
        return [.. stations.Select(s => new AccountStation(s.TenantId, s.TenantName ?? "", s.StationId, s.PublicId, s.Name, s.Role ?? ""))];
    }

    public async Task<IssuedStationTarget> GetBroadcastTargetAsync(string accessToken, Guid tenantId, Guid stationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/v1/stations/{stationId}/broadcast-target", accessToken, null,
            cancellationToken, tenantId).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var target = await ReadAsync<TargetDto>(response, cancellationToken).ConfigureAwait(false);
        if (target.StationId != stationId || string.IsNullOrEmpty(target.Username) || string.IsNullOrEmpty(target.Password)
            || target.MaxBitrateKbps <= 0 || target.Outputs is not { Count: > 0 }
            || target.Outputs.Any(o => o is null || o.IngestUrl is null || !IsAllowed(o.IngestUrl) || o.ListenerUrl is null))
        {
            throw Invalid();
        }
        return new IssuedStationTarget(new StationTarget(target.StationId, target.PublicId, target.StationName, target.Username,
            target.MaxBitrateKbps, [.. target.Outputs.Select(o => new StationOutput(o.Format, o.ContentType, o.IngestUrl, o.ListenerUrl))]),
            target.Password);
    }

    private static bool IsWebPage(Uri? url) => url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? accessToken, object? body,
        CancellationToken cancellationToken, Guid? tenantId = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        if (tenantId is { } tenant)
        {
            request.Headers.Add("X-Tenant-Id", tenant.ToString());
        }
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DesktopApiException(DesktopApiError.Unavailable, "Cannot reach the Tropicast API. Check the network and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopApiException(DesktopApiError.Unavailable, "The Tropicast API did not answer in time. Try again.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var title = (await TryReadProblemAsync(response, cancellationToken).ConfigureAwait(false))?.Title;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new DesktopApiException(DesktopApiError.Unauthorized, "Sign in again on this device."),
            HttpStatusCode.Forbidden => new DesktopApiException(DesktopApiError.Forbidden,
                title ?? "This account cannot broadcast to this station."),
            HttpStatusCode.NotFound => new DesktopApiException(DesktopApiError.NotFound, "This station no longer exists. Refresh the station list."),
            HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => new DesktopApiException(DesktopApiError.Unavailable,
                $"The Tropicast API is temporarily unavailable (HTTP {(int)response.StatusCode}). Try again shortly."),
            _ => new DesktopApiException(DesktopApiError.Invalid, title ?? $"The Tropicast API refused the request (HTTP {(int)response.StatusCode})."),
        };
    }

    private static async Task<TokenPair> ReadTokensAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var tokens = await ReadAsync<TokenDto>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tokens.AccessToken) || string.IsNullOrEmpty(tokens.RefreshToken) || tokens.ExpiresIn <= 0)
        {
            throw Invalid();
        }
        return new TokenPair(tokens.AccessToken, TimeSpan.FromSeconds(tokens.ExpiresIn), tokens.RefreshToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false) ?? throw Invalid();
        }
        catch (JsonException ex)
        {
            throw new DesktopApiException(DesktopApiError.Invalid, "The Tropicast API sent an answer this version does not understand. Update the app.", ex);
        }
    }

    private static async Task<ProblemDto?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDto>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static DesktopApiException Invalid()
        => new(DesktopApiError.Invalid, "The Tropicast API sent an answer this version does not understand. Update the app.");

    public void Dispose() => _http.Dispose();

    private sealed record DeviceCodeDto(string DeviceCode, string UserCode, Uri VerificationUri, Uri VerificationUriComplete, long ExpiresIn,
        long Interval);
    private sealed record TokenDto(string AccessToken, long ExpiresIn, string RefreshToken);
    private sealed record ProblemDto(string? Title, string? Error);
    private sealed record StationDto(Guid TenantId, string? TenantName, Guid StationId, string PublicId, string Name, string? Role);
    private sealed record OutputDto(string Format, string ContentType, Uri IngestUrl, Uri ListenerUrl);
    private sealed record TargetDto(Guid StationId, string PublicId, string StationName, string Username, string Password, int MaxBitrateKbps,
        List<OutputDto>? Outputs);
}
