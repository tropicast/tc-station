using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Tests;

namespace Tropicast.Station.Core.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void Stream_defaults_are_128_kbps_44100_hz_stereo_and_optional_metadata()
    {
        var profile = TestProfiles.Valid();
        Assert.Equal(128, profile.BitrateKbps);
        Assert.Equal(44100, profile.SampleRate);
        Assert.Equal(2, profile.Channels);
        Assert.Equal("", profile.StreamName);
        Assert.Equal("", profile.StreamDescription);
        Assert.Empty(ProfileValidator.Validate(profile));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(320)]
    public void Supported_stream_quality_options_are_valid(int bitrate)
    {
        foreach (var rate in new[] { 44100, 48000 })
        {
            foreach (var channels in new[] { 1, 2 })
            {
                Assert.Empty(ProfileValidator.Validate(TestProfiles.Valid() with
                {
                    BitrateKbps = bitrate, SampleRate = rate, Channels = channels,
                    StreamName = "Station", StreamDescription = "Local programming",
                    StreamGenre = "Talk", StreamUrl = "https://example.com/radio",
                }));
            }
        }
    }

    [Theory]
    [InlineData(160, 44100, 2)]
    [InlineData(128, 32000, 2)]
    [InlineData(128, 44100, 3)]
    public void Invalid_quality_is_rejected(int bitrate, int rate, int channels)
        => Assert.NotEmpty(ProfileValidator.Validate(TestProfiles.Valid() with
        {
            BitrateKbps = bitrate, SampleRate = rate, Channels = channels,
        }));

    [Fact]
    public void Opus_alongside_MP3_adds_an_ogg_output_on_the_opus_mount()
    {
        var profile = TestProfiles.Valid() with
        {
            Mount = "/stations/42/live.mp3", PublishOpus = true, OpusBitrateKbps = 48, SampleRate = 44100, Channels = 1,
        };
        Assert.Empty(ProfileValidator.Validate(profile));
        var outputs = profile.Outputs();
        Assert.Equal(2, outputs.Count);
        Assert.Same(profile, outputs[0]);
        var opus = outputs[1];
        Assert.Equal("/stations/42/live.opus", opus.Mount);
        Assert.Equal("audio/ogg", opus.ContentType);
        Assert.Equal(48, opus.BitrateKbps);
        Assert.Equal(48000, opus.SampleRate);
        Assert.Equal(1, opus.Channels);
        Assert.False(opus.PublishOpus);
        Assert.Empty(ProfileValidator.Validate(opus));
        Assert.Single((profile with { PublishOpus = false }).Outputs());
    }

    [Theory]
    [InlineData("audio/mpeg", "/live", 64, 128)]
    [InlineData("audio/ogg", "/live.mp3", 64, 64)]
    [InlineData("audio/mpeg", "/live.mp3", 128, 128)]
    public void Invalid_opus_settings_are_rejected(string contentType, string mount, int opusBitrate, int bitrate)
        => Assert.NotEmpty(ProfileValidator.Validate(TestProfiles.Valid() with
        {
            ContentType = contentType, Mount = mount, PublishOpus = true, OpusBitrateKbps = opusBitrate, BitrateKbps = bitrate,
        }));

    [Theory]
    [InlineData(48, true)]
    [InlineData(64, true)]
    [InlineData(96, true)]
    [InlineData(128, false)]
    public void Opus_only_profiles_use_opus_bitrates(int bitrate, bool valid)
        => Assert.Equal(valid, ProfileValidator.Validate(TestProfiles.Valid() with
        {
            ContentType = "audio/ogg", Mount = "/live.opus", BitrateKbps = bitrate,
        }).Count == 0);

    [Theory]
    [InlineData("name\r\nIce-Public: 1")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tvalue")]
    [InlineData("\0")]
    public void Metadata_control_characters_are_rejected_before_header_generation(string value)
    {
        var profile = TestProfiles.Valid();
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamName = value }));
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamDescription = value }));
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamGenre = value }));
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamUrl = value }));
    }

    [Fact]
    public void Metadata_lengths_and_nulls_are_validated()
    {
        var profile = TestProfiles.Valid();
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamName = new string('n', 129) }));
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamDescription = new string('d', 513) }));
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamGenre = new string('g', 129) }));
        Assert.NotEmpty(ProfileValidator.Validate(profile with { StreamUrl = null! }));
        Assert.Empty(ProfileValidator.Validate(profile with { StreamName = new string('n', 128), StreamDescription = new string('d', 512) }));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")]
    [InlineData("https://user:password@example.com")]
    public void Invalid_metadata_urls_are_rejected(string url)
        => Assert.NotEmpty(ProfileValidator.Validate(TestProfiles.Valid() with { StreamUrl = url }));

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
