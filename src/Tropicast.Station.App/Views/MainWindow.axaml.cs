using Avalonia.Controls;
using Tropicast.Station.App.ViewModels;

namespace Tropicast.Station.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainViewModel model)
        {
            model.Profiles.LoadCommand.Execute(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.Profiles.TestCommand.Cancel();
            model.Profiles.Password = "";
        }

        base.OnClosed(e);
    }
}
