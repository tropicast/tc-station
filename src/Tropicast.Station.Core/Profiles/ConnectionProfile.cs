using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Tropicast.Station.Core.Profiles;

/// <summary>Non-secret connection settings. Passwords are keyed by Id in the OS secure store.</summary>
public sealed record ConnectionProfile(
    Guid Id, string Name, string Host, int Port, string Mount,
    string Username = "source", bool UseTls = false, string ContentType = "audio/mpeg")
{
    [JsonIgnore]
    public Uri Endpoint => new UriBuilder(UseTls ? "https" : "http", Host, Port, Mount).Uri;
}

public static partial class ProfileValidator
{
    public static IReadOnlyList<string> Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>();
        if (profile.Id == Guid.Empty)
        {
            errors.Add("Profile ID is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 64 || profile.Name.Any(char.IsControl))
        {
            errors.Add("Name is required and must be at most 64 characters without control characters.");
        }

        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Host.Length > 253
            || profile.Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')
            || (Uri.CheckHostName(profile.Host) != UriHostNameType.Dns && !IPAddress.TryParse(profile.Host, out _)))
        {
            errors.Add("Host must be a hostname or IP address, without a scheme, port or path.");
        }

        if (profile.Port is < 1 or > 65535)
        {
            errors.Add("Port must be between 1 and 65535.");
        }

        if (string.IsNullOrEmpty(profile.Mount) || profile.Mount.Length > 128 || !MountPattern().IsMatch(profile.Mount)
            || profile.Mount.Split('/').Any(s => s is "." or "..")
            || profile.Mount.Equals("/admin", StringComparison.OrdinalIgnoreCase)
            || profile.Mount.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Mount must start with '/', contain only URL-safe path characters, and not use /admin or dot segments.");
        }

        if (string.IsNullOrWhiteSpace(profile.Username) || profile.Username.Length > 64
            || profile.Username.Any(c => c == ':' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            errors.Add("Username is required and must not contain ':' or whitespace.");
        }

        if (profile.ContentType is not ("audio/mpeg" or "audio/aac" or "audio/ogg"))
        {
            errors.Add("Select audio/mpeg, audio/aac or audio/ogg.");
        }

        return errors;
    }

    public static bool IsValidPassword(string? password)
        => !string.IsNullOrEmpty(password) && password.Length <= 256 && !password.Any(char.IsControl);

    [GeneratedRegex(@"^/[A-Za-z0-9._~/-]*[A-Za-z0-9._~-]$")]
    private static partial Regex MountPattern();
}
