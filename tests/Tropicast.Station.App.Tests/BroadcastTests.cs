using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Encoding;
using Tropicast.Station.Tests;

namespace Tropicast.Station.App.Tests;

public sealed class BroadcastTests
{
    [AvaloniaFact]
    public async Task Three_step_flow_locks_settings_and_stop_requires_confirmation()
    {
        var profile = TestProfiles.Valid();
        var encoder = new FakeEncoder();
        var confirmation = new Confirmation();
        using var host = CreateHost(profile, encoder, confirmation);
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var model = host.Services.GetRequiredService<MainViewModel>();
        using var tray = new BroadcastTray(window, model.Broadcast);
        await model.Profiles.LoadCommand.ExecuteAsync(null);
        await model.Audio.RefreshCommand.ExecuteAsync(null);
        var button = window.FindControl<Button>("GoLiveButton")!;
        Assert.False(model.Broadcast.GoLiveCommand.CanExecute(null));
        Assert.Equal("Go Live", AutomationProperties.GetName(button));
        model.Profiles.SelectedProfile = profile;
        Assert.False(model.Broadcast.GoLiveCommand.CanExecute(null));
        model.Audio.SelectedInput = model.Audio.Inputs[0];
        Assert.True(model.Broadcast.GoLiveCommand.CanExecute(null));
        Assert.True(button.Focusable);
        await model.Broadcast.GoLiveCommand.ExecuteAsync(null);
        await UntilAsync(() => model.Broadcast.State == BroadcastState.Live);
        await UntilAsync(() => model.Audio.Levels.ChannelLevels.Count == 2
            && model.Audio.Levels.ChannelLevels[0].Peak > -20);
        Assert.True(model.Audio.Levels.IsActive);
        Assert.False(model.Profiles.CanManage);
        Assert.False(model.Profiles.TestCommand.CanExecute(null));
        Assert.False(model.Profiles.SaveCommand.CanExecute(null));
        Assert.False(model.Audio.CanChoose);
        Assert.False(model.Audio.StopCommand.CanExecute(null));
        Assert.False(window.FindControl<ComboBox>("BroadcastProfilePicker")!.IsEnabled);
        Assert.True(window.FindControl<Button>("StopBroadcastButton")!.IsVisible);
        Assert.Equal("Live", window.FindControl<TextBlock>("BroadcastState")!.Text);
        Assert.Contains("Live", Assert.Single(TrayIcon.GetIcons(Application.Current!)!).ToolTipText!, StringComparison.Ordinal);
        confirmation.Allow = false;
        await model.Broadcast.StopCommand.ExecuteAsync(null);
        Assert.Equal(BroadcastState.Live, model.Broadcast.State);
        Assert.Equal(0, encoder.Session.Disposals);
        confirmation.Allow = true;
        await model.Broadcast.StopCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(BroadcastState.Idle, model.Broadcast.State);
        model.Audio.Levels.Refresh();
        Assert.False(model.Audio.Levels.IsActive);
        Assert.True(model.Profiles.CanManage);
        Assert.True(model.Audio.CanChoose);
        Assert.Equal(1, encoder.Session.Disposals);
        Assert.Equal(2, confirmation.Requests);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_can_start_and_stop_the_broadcast()
    {
        var profile = TestProfiles.Valid();
        using var host = CreateHost(profile, new FakeEncoder(), new Confirmation { Allow = true });
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var model = host.Services.GetRequiredService<MainViewModel>();
        await model.Profiles.LoadCommand.ExecuteAsync(null);
        await model.Audio.RefreshCommand.ExecuteAsync(null);
        model.Profiles.SelectedProfile = profile;
        model.Audio.SelectedInput = model.Audio.Inputs[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Button>("GoLiveButton")!.Focus());
        window.KeyPress(Key.Enter, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Enter, null);
        await UntilAsync(() => model.Broadcast.State == BroadcastState.Live);
        Assert.True(window.FindControl<Button>("StopBroadcastButton")!.Focus());
        window.KeyPress(Key.Enter, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Enter, null);
        await UntilAsync(() => model.Broadcast.State == BroadcastState.Idle);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Real_confirmation_dialog_defaults_to_keep_and_escape_does_not_stop()
    {
        var window = new Window();
        window.Show();
        var confirmation = new BroadcastConfirmation { Owner = window };
        var pending = confirmation.ConfirmStopAsync(false);
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(await pending);
        pending = confirmation.ConfirmStopAsync(true);
        dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Stop broadcast and quit?", dialog.Title);
        var panel = Assert.IsType<StackPanel>(dialog.Content);
        var buttons = Assert.IsType<StackPanel>(panel.Children[1]);
        Assert.True(Assert.IsType<Button>(buttons.Children[1]).Focus());
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(await pending);
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_live_window_can_be_cancelled_then_confirmed_and_stops_encoder()
    {
        var profile = TestProfiles.Valid();
        var encoder = new FakeEncoder();
        var confirmation = new Confirmation();
        using var host = CreateHost(profile, encoder, confirmation);
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var model = host.Services.GetRequiredService<MainViewModel>();
        await model.Profiles.LoadCommand.ExecuteAsync(null);
        await model.Audio.RefreshCommand.ExecuteAsync(null);
        model.Profiles.SelectedProfile = profile;
        model.Audio.SelectedOutput = model.Audio.Outputs[0];
        await model.Broadcast.GoLiveCommand.ExecuteAsync(null);
        await UntilAsync(() => model.Broadcast.State == BroadcastState.Live);
        window.Close();
        await UntilAsync(() => confirmation.Requests == 1);
        Assert.True(window.IsVisible);
        Assert.True(model.Broadcast.HasActiveBroadcast);
        Assert.True(confirmation.LastClosing);
        confirmation.Allow = true;
        window.Close();
        await UntilAsync(() => !window.IsVisible);
        Assert.False(model.Broadcast.HasActiveBroadcast);
        Assert.Equal(1, encoder.Session.Disposals);
    }

    [AvaloniaFact]
    public async Task Encoder_error_is_visible_and_unlocks_profile_and_audio_selection()
    {
        var profile = TestProfiles.Valid();
        var encoder = new FakeEncoder { StartError = new IOException("Authentication failed.") };
        using var host = CreateHost(profile, encoder, new Confirmation());
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var model = host.Services.GetRequiredService<MainViewModel>();
        await model.Profiles.LoadCommand.ExecuteAsync(null);
        await model.Audio.RefreshCommand.ExecuteAsync(null);
        model.Profiles.SelectedProfile = profile;
        model.Audio.SelectedInput = model.Audio.Inputs[0];
        await model.Broadcast.GoLiveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(BroadcastState.Error, model.Broadcast.State);
        Assert.Contains("Authentication", model.Broadcast.Status, StringComparison.Ordinal);
        Assert.True(model.Profiles.CanManage);
        Assert.True(model.Audio.CanChoose);
        Assert.True(model.Broadcast.GoLiveCommand.CanExecute(null));
        Assert.Equal("Error", window.FindControl<TextBlock>("BroadcastState")!.Text);
        window.Close();
    }

    private static Microsoft.Extensions.Hosting.IHost CreateHost(ConnectionProfile profile, FakeEncoder encoder, Confirmation confirmation)
    {
        var secrets = new MemorySecrets();
        secrets.Items[profile.Id] = "test-only";
        return AppHost.Create([], services =>
        {
            services.AddSingleton<IProfileStore>(new MemoryProfiles { Items = [profile] });
            services.AddSingleton<ISecretStore>(secrets);
            services.AddSingleton<IAudioCaptureProvider, ToneAudioCaptureProvider>();
            services.AddSingleton<IBroadcastEncoder>(encoder);
            services.AddSingleton<IBroadcastConfirmation>(confirmation);
        });
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, deadline.Token);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Confirmation : IBroadcastConfirmation
    {
        internal bool Allow { get; set; }
        internal int Requests { get; private set; }
        internal bool LastClosing { get; private set; }
        public Task<bool> ConfirmStopAsync(bool closing)
        {
            Requests++;
            LastClosing = closing;
            return Task.FromResult(Allow);
        }
    }

    private sealed class FakeEncoder : IBroadcastEncoder
    {
        internal Session Session { get; } = new();
        internal IOException? StartError { get; init; }
        public Task<IEncoderSession> StartAsync(BroadcastTarget target, EncoderOptions? options = null, CancellationToken cancellationToken = default)
            => StartError is { } error ? Task.FromException<IEncoderSession>(error) : Task.FromResult<IEncoderSession>(Session);
    }

    private sealed class Session : IEncoderSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EncoderSnapshot _snapshot = new(EncoderState.Starting, "Starting", 0);
        internal int Disposals { get; private set; }
        public EncoderSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public Task Completion => _completion.Task;
        public void Submit(PcmFrame frame) => Volatile.Write(ref _snapshot, new(EncoderState.Streaming, "Live", frame.Data.Length));
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult();
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            await StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
