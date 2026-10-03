using System.Text.Json;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Infrastructure;

public sealed class JsonProfileStore(string directory) : IProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(directory, "profiles.json");

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var input = File.OpenRead(_path);
            var profiles = await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(input, Options, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The profiles file is empty.");
            if (profiles.Any(p => p is null || ProfileValidator.Validate(p).Count > 0)
                || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
            {
                throw new IOException("The profiles file contains invalid or duplicate profiles. Restore it from a backup.");
            }

            return profiles;
        }
        catch (JsonException)
        {
            throw new IOException("The profiles file is corrupt. Restore it from a backup.");
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Any(p => ProfileValidator.Validate(p).Count > 0)
            || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
        {
            throw new ArgumentException("Cannot persist invalid or duplicate profiles.", nameof(profiles));
        }

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".profiles-{Guid.NewGuid():N}.tmp");
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var output = new FileStream(temporary, fileOptions))
            {
                await JsonSerializer.SerializeAsync(output, profiles, Options, cancellationToken).ConfigureAwait(false);
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
