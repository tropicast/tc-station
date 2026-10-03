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
        });
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var editor = host.Services.GetRequiredService<ProfileEditorViewModel>();
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Name = "Studio";
        editor.Password = "sentinel-source-password";
        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", editor.Password);
        Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("PasswordInput")?.Text));
        Assert.Equal("sentinel-source-password", Assert.Single(secrets.Items).Value);
        await editor.EditCommand.ExecuteAsync(null);
        Assert.Equal("", editor.Password);
        Assert.Contains("Leave blank", editor.PasswordHint, StringComparison.Ordinal);
        editor.Port = "not a port";
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.TestCommand.CanExecute(null));
        editor.Port = "8000";
        Assert.True(editor.SaveCommand.CanExecute(null));
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
        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal("/edited.mp3", tester.Target?.Profile.Mount);
        Assert.Equal("sentinel-password", tester.Target?.Password);
        Assert.Equal("", editor.Password);
        Assert.Equal("Accepted", editor.Status);
        Assert.Equal("/live.mp3", Assert.Single(profiles.Items).Mount);
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
