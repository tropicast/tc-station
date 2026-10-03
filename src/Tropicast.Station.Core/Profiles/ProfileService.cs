namespace Tropicast.Station.Core.Profiles;

public sealed class ProfileService(IProfileStore profiles, ISecretStore secrets)
{
    public Task<string?> GetPasswordAsync(Guid id, CancellationToken cancellationToken = default)
        => secrets.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
        => profiles.LoadAsync(cancellationToken);

    public async Task<bool> HasPasswordAsync(Guid id, CancellationToken cancellationToken = default)
        => ProfileValidator.IsValidPassword(await secrets.GetAsync(id, cancellationToken).ConfigureAwait(false));

    public async Task SaveAsync(ConnectionProfile profile, string? replacementPassword, CancellationToken cancellationToken = default)
    {
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        }

        var saved = (await profiles.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (saved.Any(p => p.Id != profile.Id && p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Another profile already uses this name.", nameof(profile));
        }

        var oldPassword = await secrets.GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        if (!ProfileValidator.IsValidPassword(replacementPassword ?? oldPassword))
        {
            throw new ArgumentException("A password of 1–256 characters without control characters is required.", nameof(replacementPassword));
        }

        if (replacementPassword is not null)
        {
            await secrets.SetAsync(profile.Id, replacementPassword, cancellationToken).ConfigureAwait(false);
        }

        saved.RemoveAll(p => p.Id == profile.Id);
        saved.Add(profile);
        try
        {
            await profiles.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Roll back the secure-store update when the settings write fails.
            try
            {
                if (oldPassword is null)
                {
                    await secrets.DeleteAsync(profile.Id, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await secrets.SetAsync(profile.Id, oldPassword, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (SecretStoreException rollbackError)
            {
                throw new AggregateException("Saving the profile failed and its password could not be restored.", saveError, rollbackError);
            }

            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var saved = (await profiles.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var oldPassword = await secrets.GetAsync(id, cancellationToken).ConfigureAwait(false);
        await secrets.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        saved.RemoveAll(p => p.Id == id);
        try
        {
            await profiles.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (oldPassword is not null)
            {
                try
                {
                    await secrets.SetAsync(id, oldPassword, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SecretStoreException rollbackError)
                {
                    throw new AggregateException("Deleting the profile failed and its password could not be restored.", saveError, rollbackError);
                }
            }

            throw;
        }
    }
}
