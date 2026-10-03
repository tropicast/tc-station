using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Tests;

internal sealed class MemoryProfiles : IProfileStore
{
    public IReadOnlyList<ConnectionProfile> Items { get; set; } = [];
    public bool FailWrites { get; set; }

    public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Items);

    public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new IOException("Simulated failure");
        }

        Items = profiles.ToArray();
        return Task.CompletedTask;
    }
}

internal sealed class MemorySecrets : ISecretStore
{
    public Dictionary<Guid, string> Items { get; } = [];
    public bool Unavailable { get; set; }

    public Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (Unavailable)
        {
            throw new SecretStoreException("Unlock the test keyring.");
        }

        return Task.FromResult(Items.GetValueOrDefault(profileId));
    }

    public Task SetAsync(Guid profileId, string password, CancellationToken cancellationToken = default)
    {
        Items[profileId] = password;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        Items.Remove(profileId);
        return Task.CompletedTask;
    }
}

internal static class TestProfiles
{
    internal static ConnectionProfile Valid() => new(Guid.NewGuid(), "Test station", "localhost", 8000, "/live.mp3");
}
