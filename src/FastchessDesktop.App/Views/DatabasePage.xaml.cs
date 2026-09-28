using System.Globalization;
using FastchessDesktop.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FastchessDesktop.App.Views;

public sealed partial class DatabasePage : Page
{
    public DatabasePage() => InitializeComponent();

    public DatabaseViewModel ViewModel { get; } = App.Shell.Database;

    // Formatters used through x:Bind function binding in the ratings list.
    public static string Format(double rating) => rating.ToString("0", CultureInfo.CurrentCulture);

    public static string FormatError(double? error) => error?.ToString("0.0", CultureInfo.CurrentCulture) ?? "";

    public static string FormatCount(long count) => count.ToString(CultureInfo.CurrentCulture);

    private void OnGamesSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection([.. GamesList.SelectedItems.OfType<GameRow>()]);

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.SearchCommand.Execute(null);
    }
}
