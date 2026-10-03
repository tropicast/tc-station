using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Tests;

namespace Tropicast.Station.Core.Tests;

public sealed class ProfileTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("radio.example.com")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Valid_hosts(string host) => Assert.Empty(ProfileValidator.Validate(TestProfiles.Valid() with { Host = host }));

    [Theory]
    [InlineData("https://radio.example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com:8000")]
    [InlineData("host\r\nInjected: yes")]
    [InlineData("")]
    public void Invalid_hosts(string host) => Assert.NotEmpty(ProfileValidator.Validate(TestProfiles.Valid() with { Host = host }));

    [Theory]
    [InlineData("live.mp3")]
    [InlineData("/")]
    [InlineData("/admin")]
    [InlineData("/ADMIN/listclients")]
    [InlineData("/../live.mp3")]
    [InlineData("/a?b")]
    [InlineData("/a b")]
    [InlineData("/a\r\n")]
    public void Invalid_mounts(string mount) => Assert.NotEmpty(ProfileValidator.Validate(TestProfiles.Valid() with { Mount = mount }));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void Port_limits(int port, bool valid)
        => Assert.Equal(valid, ProfileValidator.Validate(TestProfiles.Valid() with { Port = port }).Count == 0);

    [Theory]
    [InlineData("")]
    [InlineData("bad:username")]
    [InlineData("user\r\n")]
    public void Invalid_usernames(string username)
        => Assert.NotEmpty(ProfileValidator.Validate(TestProfiles.Valid() with { Username = username }));

    [Fact]
    public async Task Save_edit_keep_password_and_delete()
    {
        var store = new MemoryProfiles();
        var secrets = new MemorySecrets();
        var service = new ProfileService(store, secrets);
        var profile = TestProfiles.Valid();
        await service.SaveAsync(profile, "source-secret", TestContext.Current.CancellationToken);
        Assert.Equal("source-secret", secrets.Items[profile.Id]);
        await service.SaveAsync(profile with { Port = 9000 }, null, TestContext.Current.CancellationToken);
        Assert.Equal("source-secret", secrets.Items[profile.Id]);
        Assert.Equal(9000, Assert.Single(store.Items).Port);
        await service.DeleteAsync(profile.Id, TestContext.Current.CancellationToken);
        Assert.Empty(store.Items);
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Failed_settings_write_restores_original_password()
    {
        var profile = TestProfiles.Valid();
        var store = new MemoryProfiles { Items = [profile], FailWrites = true };
        var secrets = new MemorySecrets();
        secrets.Items[profile.Id] = "old-secret";
        var service = new ProfileService(store, secrets);
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(profile, "new-secret", TestContext.Current.CancellationToken));
        Assert.Equal("old-secret", secrets.Items[profile.Id]);
        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(profile.Id, TestContext.Current.CancellationToken));
        Assert.Equal("old-secret", secrets.Items[profile.Id]);
    }

    [Fact]
    public async Task New_failed_save_removes_orphan_secret()
    {
        var secrets = new MemorySecrets();
        var service = new ProfileService(new MemoryProfiles { FailWrites = true }, secrets);
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(TestProfiles.Valid(), "secret", TestContext.Current.CancellationToken));
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Invalid_duplicate_and_missing_password_profiles_are_rejected()
    {
        var profile = TestProfiles.Valid();
        var store = new MemoryProfiles { Items = [profile] };
        var service = new ProfileService(store, new MemorySecrets());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(profile with { Port = 0 }, "secret", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(profile with { Id = Guid.NewGuid() }, "secret", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(profile, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Broadcast_provider_validates_saved_profile_and_redacts_target()
    {
        var profile = TestProfiles.Valid();
        var store = new MemoryProfiles { Items = [profile] };
        var secrets = new MemorySecrets();
        var provider = new ManualBroadcastTargetProvider(store, secrets);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(profile.Id, TestContext.Current.CancellationToken));
        secrets.Items[profile.Id] = "unique-sensitive-value";
        var target = await provider.GetAsync(profile.Id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(target.Password, target.ToString());
        Assert.DoesNotContain(target.Password, System.Text.Json.JsonSerializer.Serialize(target));
        Assert.Empty(target.Profile.Endpoint.UserInfo);
        store.Items = [profile with { Port = 0 }];
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(profile.Id, TestContext.Current.CancellationToken));
    }
}
