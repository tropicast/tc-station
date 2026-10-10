using System.Text.Json;
using Tropicast.Station.Core.Account;

namespace Tropicast.Station.Infrastructure;

/// <summary>
/// Account state without secrets (stations and broadcast targets) in <c>account.json</c>. It is a cache: a missing
/// or unreadable file means signing in again or refreshing the station list, never a failure.
/// </summary>
public sealed class JsonAccountStore(string directory) : IAccountStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(directory, "account.json");

    public async Task<AccountData> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var input = File.OpenRead(_path);
            var data = await JsonSerializer.DeserializeAsync<AccountData>(input, Options, cancellationToken).ConfigureAwait(false);
            return data is { Stations: not null, Targets: not null } ? data : AccountData.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return AccountData.Empty;
        }
    }

    public async Task SaveAsync(AccountData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".account-{Guid.NewGuid():N}.tmp");
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var output = new FileStream(temporary, fileOptions))
            {
                await JsonSerializer.SerializeAsync(output, data, Options, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
