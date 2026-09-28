using FastchessDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastchessDesktop.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    public SettingsViewModel ViewModel { get; } = App.Shell.Settings;

    private void OnSaveClick(object sender, RoutedEventArgs e) => App.Shell.SaveSettings();
}
