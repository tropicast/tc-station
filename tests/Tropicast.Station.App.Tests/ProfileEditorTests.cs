using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Tests;
using Tropicast.Station.Audio;

namespace Tropicast.Station.App.Tests;

public sealed class ProfileEditorTests
{
    [AvaloniaFact]
    public async Task Save_clears_password_in_view_and_edit_does_not_reveal_it()
    {
        var profiles = new MemoryProfiles();
        var secrets = new MemorySecrets();
        using var host = AppHost.Create([], services =>
        {
            services.AddSingleton<IProfileStore>(profiles);
            services.AddSingleton<ISecretStore>(secrets);
            services.AddSingleton<IAudioCaptureProvider, ToneAudioCaptureProvider>();
        });
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var editor = host.Services.GetRequiredService<ProfileEditorViewModel>();
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Name = "Studio";
        editor.Password = "sentinel-source-password";
        Assert.Equal(128, editor.BitrateKbps);
        Assert.Equal(44100, editor.SampleRate);
        Assert.Equal(2, editor.Channels);
        editor.BitrateKbps = 192;
        editor.SampleRate = 48000;
        editor.Channels = 1;
        editor.StreamName = "Studio radio";
        editor.StreamDescription = "Local programming";
        editor.StreamGenre = "Talk";
        editor.StreamUrl = "https://example.com/studio";
        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", editor.Password);
        Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("PasswordInput")?.Text));
        Assert.Equal("sentinel-source-password", Assert.Single(secrets.Items).Value);
        await editor.EditCommand.ExecuteAsync(null);
        Assert.Equal("", editor.Password);
        Assert.Contains("Leave blank", editor.PasswordHint, StringComparison.Ordinal);
        Assert.Equal(192, editor.BitrateKbps);
        Assert.Equal(48000, editor.SampleRate);
        Assert.Equal(1, editor.Channels);
        Assert.Equal("Studio radio", editor.StreamName);
        Assert.Equal("Local programming", editor.StreamDescription);
        Assert.Equal("Talk", editor.StreamGenre);
        Assert.Equal("https://example.com/studio", editor.StreamUrl);
        window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2;
        window.UpdateLayout();
        Assert.Equal(192, window.FindControl<ComboBox>("ProfileBitrate")!.SelectedItem);
        Assert.Equal("Studio radio", window.FindControl<TextBox>("StreamNameInput")!.Text);
        editor.StreamDescription = "bad\r\nheader";
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.StreamDescription = "Local programming";
        editor.Port = "not a port";
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.TestCommand.CanExecute(null));
        editor.Port = "8000";
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.NewCommand.Execute(null);
        Assert.Equal(128, editor.BitrateKbps);
        Assert.Equal(44100, editor.SampleRate);
        Assert.Equal(2, editor.Channels);
        Assert.Equal("", editor.StreamName);
        window.Close();
    }

    [Fact]
    public async Task Test_uses_unsaved_edits_and_stored_password_without_exposing_it()
    {
        var profile = TestProfiles.Valid();
        var profiles = new MemoryProfiles { Items = [profile] };
        var secrets = new MemorySecrets();
        secrets.Items[profile.Id] = "sentinel-password";
        var tester = new CapturingTester();
        var editor = new ProfileEditorViewModel(new(profiles, secrets), tester, NullLogger<ProfileEditorViewModel>.Instance);
        await editor.LoadCommand.ExecuteAsync(null);
        editor.SelectedProfile = profile;
        await editor.EditCommand.ExecuteAsync(null);
        editor.Mount = "/edited.mp3";
        editor.BitrateKbps = 320;
        editor.StreamName = "Unsaved stream name";
        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal("/edited.mp3", tester.Target?.Profile.Mount);
        Assert.Equal(320, tester.Target?.Profile.BitrateKbps);
        Assert.Equal("Unsaved stream name", tester.Target?.Profile.StreamName);
        Assert.Equal("sentinel-password", tester.Target?.Password);
        Assert.Equal("", editor.Password);
        Assert.Equal("Accepted", editor.Status);
        Assert.Equal("/live.mp3", Assert.Single(profiles.Items).Mount);
        Assert.Equal(128, Assert.Single(profiles.Items).BitrateKbps);
    }

    [Fact]
    public async Task Unavailable_keyring_is_visible_and_logs_do_not_contain_passwords()
    {
        var logger = new CapturingLogger();
        var secrets = new MemorySecrets { Unavailable = true };
        var editor = new ProfileEditorViewModel(new(new MemoryProfiles(), secrets), new CapturingTester(), logger)
        {
            Name = "Studio",
            Password = "sentinel-password",
        };
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Unlock", editor.Status, StringComparison.Ordinal);
        Assert.Single(logger.Messages);
        Assert.DoesNotContain("sentinel-password", string.Join("\n", logger.Messages), StringComparison.Ordinal);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task Delete_removes_both_profile_and_secret()
    {
        var profile = TestProfiles.Valid();
        var profiles = new MemoryProfiles { Items = [profile] };
        var secrets = new MemorySecrets();
        secrets.Items[profile.Id] = "secret";
        var editor = new ProfileEditorViewModel(new(profiles, secrets), new CapturingTester(), NullLogger<ProfileEditorViewModel>.Instance);
        await editor.LoadCommand.ExecuteAsync(null);
        editor.SelectedProfile = profile;
        await editor.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(editor.Profiles);
        Assert.Empty(secrets.Items);
    }

    private sealed class CapturingTester : IConnectionTester
    {
        public BroadcastTarget? Target { get; private set; }
        public Task<ConnectionTestResult> TestAsync(BroadcastTarget target, CancellationToken cancellationToken = default)
        {
            Target = target;
            return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.Accepted, "Accepted"));
        }
    }

    private sealed class CapturingLogger : ILogger<ProfileEditorViewModel>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
