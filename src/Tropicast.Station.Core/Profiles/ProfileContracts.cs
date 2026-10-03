namespace Tropicast.Station.Core.Profiles;

public interface IProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task SetAsync(Guid profileId, string password, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public sealed class SecretStoreException : Exception
{
    public SecretStoreException() : base("The OS credential store is unavailable.") { }

    public SecretStoreException(string message) : base(message) { }

    public SecretStoreException(string message, Exception innerException) : base(message, innerException) { }
}
