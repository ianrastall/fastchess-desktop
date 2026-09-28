using FastchessDesktop.App.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace FastchessDesktop.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Caption buttons stay readable on the dark Mica background.
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.White;
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Colors.White;

        AppWindow.Resize(new SizeInt32(1600, 1000));
    }

    /// <summary>Shows the first page. Called once App.Shell exists.</summary>
    public void ShowShell() => Navigation.SelectedItem = Navigation.MenuItems[0];

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var page = args.IsSettingsSelected ? typeof(SettingsPage)
            : tag == "Database" ? typeof(DatabasePage)
            : typeof(TournamentPage);
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
    }
}
