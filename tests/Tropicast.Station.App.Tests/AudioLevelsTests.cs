using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Audio;

namespace Tropicast.Station.App.Tests;

public sealed class AudioLevelsTests
{
    [AvaloniaFact]
    public async Task Preview_feeds_normalized_stereo_meters_without_broadcast_and_stop_clears_them()
    {
        using var host = AppHost.Create(["--demo-audio"]);
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var audio = host.Services.GetRequiredService<AudioDevicesViewModel>();
        await audio.RefreshCommand.ExecuteAsync(null);
        audio.SelectedInput = audio.Inputs[0];
        await audio.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            if (audio.Levels.ChannelLevels.Count == 2 && audio.Levels.ChannelLevels[0].Peak > -20)
            {
                break;
            }
        }
        Assert.True(audio.Levels.IsActive);
        Assert.Equal(2, audio.Levels.ChannelLevels.Count);
        Assert.Equal("Left", audio.Levels.ChannelLevels[0].Name);
        Assert.Equal("Right", audio.Levels.ChannelLevels[1].Name);
        Assert.InRange(audio.Levels.ChannelLevels[0].Peak, -14.1, -13.9);
        var view = window.FindControl<AudioLevelsView>("BroadcastLevels")!;
        Assert.Equal(2, view.FindControl<ItemsControl>("ChannelMeters")!.Items.Count);
        Assert.Contains("Monitoring", view.FindControl<TextBlock>("LevelStatus")!.Text!, StringComparison.Ordinal);
        Assert.False(host.Services.GetRequiredService<BroadcastViewModel>().HasActiveBroadcast);
        await audio.StopCommand.ExecuteAsync(null);
        audio.Levels.Refresh();
        Assert.False(audio.Levels.IsActive);
        Assert.Empty(audio.Levels.ChannelLevels);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Silence_is_visible_and_optional_notification_fires_once_per_episode()
    {
        using var host = AppHost.Create(["--demo-audio"]);
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var levels = host.Services.GetRequiredService<AudioLevelsViewModel>();
        var meter = host.Services.GetRequiredService<AudioCaptureService>().Levels;
        levels.SilenceSeconds = 1;
        var notifications = 0;
        levels.SilenceStarted += (_, _) => notifications++;
        meter.Start(AudioFormat.EncoderDefault);
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        levels.Refresh();
        Assert.True(levels.IsSilent);
        Assert.Equal(0, notifications);
        Assert.Contains("Silence warning", levels.Status, StringComparison.Ordinal);
        levels.NotifyOnSilence = true;
        Feed(meter, 0.1f);
        levels.Refresh();
        Assert.False(levels.IsSilent);
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        levels.Refresh();
        Assert.True(levels.IsSilent);
        Assert.Equal(1, notifications);
        levels.Refresh();
        Assert.Equal(1, notifications);
        var view = window.FindControl<AudioLevelsView>("BroadcastLevels")!;
        Assert.Contains("Silence warning", view.FindControl<TextBlock>("LevelStatus")!.Text!, StringComparison.Ordinal);
        levels.SilenceThreshold = -60;
        Assert.False(levels.IsSilent);
        meter.Stop();
        levels.Refresh();
        Assert.False(levels.IsSilent);
        window.Close();
    }

    [AvaloniaFact]
    public void Clipping_hold_is_visible_and_new_mono_capture_resets_stereo_levels()
    {
        using var host = AppHost.Create(["--demo-audio"]);
        var model = host.Services.GetRequiredService<AudioLevelsViewModel>();
        var meter = host.Services.GetRequiredService<AudioCaptureService>().Levels;
        meter.Start(AudioFormat.EncoderDefault);
        Feed(meter, -1);
        model.Refresh();
        Assert.All(model.ChannelLevels, channel => Assert.True(channel.IsClipping));
        model.Refresh();
        Assert.All(model.ChannelLevels, channel => Assert.True(channel.IsClipping));
        meter.Stop();
        meter.Start(new(44100, 1));
        model.Refresh();
        Assert.Equal("Mono", Assert.Single(model.ChannelLevels).Name);
        Assert.False(model.ChannelLevels[0].IsClipping);
        model.Dispose();
        Feed(meter, 1, 1, 44100);
        model.Refresh();
        Assert.False(model.ChannelLevels[0].IsClipping);
    }

    [AvaloniaFact]
    public void Expanded_meter_settings_do_not_push_primary_control_outside_small_window()
    {
        using var host = AppHost.Create(["--demo-audio"]);
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Width = 640;
        window.Height = 420;
        window.Show();
        var meterView = window.FindControl<AudioLevelsView>("BroadcastLevels")!;
        meterView.FindControl<Expander>("SilenceSettings")!.IsExpanded = true;
        window.UpdateLayout();
        var button = window.FindControl<Button>("GoLiveButton")!;
        var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), window);
        Assert.NotNull(bottom);
        Assert.True(button.Bounds.Height > 0);
        Assert.InRange(bottom.Value.Y, 0, window.ClientSize.Height);
        window.Close();
    }

    private static void Feed(AudioLevelMeter meter, float value, int channels = 2, int rate = 48000)
    {
        var bytes = new byte[480 * channels * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), value);
        }
        meter.Process(new(new(rate, channels), bytes));
    }
}
