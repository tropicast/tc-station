using Tropicast.Station.Tests;

namespace Tropicast.Station.Infrastructure.Tests;

public sealed class ProfileStorageTests
{
    [Fact]
    public async Task Round_trip_only_non_secret_settings_with_private_file_permissions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tc-profiles-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonProfileStore(directory);
            Assert.Empty(await store.LoadAsync(TestContext.Current.CancellationToken));
            var profile = TestProfiles.Valid();
            await store.SaveAsync([profile], TestContext.Current.CancellationToken);
            Assert.Equal(profile, Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken)));
            var path = Path.Combine(directory, "profiles.json");
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Endpoint", json, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(directory));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Os_store_round_trip_replace_and_delete()
    {
        // Enabled explicitly where a real unlocked keyring/credential store is available.
        if (Environment.GetEnvironmentVariable("TC_TEST_OS_SECRETS") != "1")
        {
            Assert.Skip("Set TC_TEST_OS_SECRETS=1 to exercise the real OS credential store.");
        }

        var store = new OsSecretStore();
        var id = Guid.NewGuid();
        try
        {
            Assert.Null(await store.GetAsync(id, TestContext.Current.CancellationToken));
            await store.SetAsync(id, "test-secret-!é", TestContext.Current.CancellationToken);
            Assert.Equal("test-secret-!é", await store.GetAsync(id, TestContext.Current.CancellationToken));
            await store.SetAsync(id, "replacement-secret", TestContext.Current.CancellationToken);
            Assert.Equal("replacement-secret", await store.GetAsync(id, TestContext.Current.CancellationToken));
            await store.DeleteAsync(id, TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync(id, TestContext.Current.CancellationToken));
            await store.DeleteAsync(id, TestContext.Current.CancellationToken);
        }
        finally
        {
            await store.DeleteAsync(id, CancellationToken.None);
        }
    }
}
