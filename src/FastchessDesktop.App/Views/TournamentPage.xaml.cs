using FastchessDesktop.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FastchessDesktop.App.Views;

public sealed partial class TournamentPage : Page
{
    public TournamentPage() => InitializeComponent();

    public TournamentViewModel ViewModel { get; } = App.Shell.Tournament;
}
