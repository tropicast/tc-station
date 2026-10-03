using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Tropicast.Station.App;

public interface IBroadcastConfirmation
{
    Task<bool> ConfirmStopAsync(bool closing);
}

internal sealed class BroadcastConfirmation : IBroadcastConfirmation
{
    internal Window? Owner { get; set; }

    public async Task<bool> ConfirmStopAsync(bool closing)
    {
        if (Owner is not { IsVisible: true } owner)
        {
            throw new InvalidOperationException("Broadcast confirmation needs an open main window.");
        }
        owner.WindowState = WindowState.Normal;
        owner.Activate();
        var dialog = new Window
        {
            Title = closing ? "Stop broadcast and quit?" : "Stop broadcast?",
            Width = 420, Height = 190, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var keep = new Button { Content = "Keep broadcasting", IsCancel = true };
        var stop = new Button { Content = closing ? "Stop and quit" : "Stop broadcast" };
        keep.Click += (_, _) => dialog.Close(false);
        stop.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Your station is broadcasting. Stopping disconnects its live stream.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { keep, stop } },
            },
        };
        dialog.Opened += (_, _) => keep.Focus();
        return await dialog.ShowDialog<bool>(owner);
    }
}
