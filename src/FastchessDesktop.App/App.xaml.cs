using FastchessDesktop.App.Services;
using FastchessDesktop.Core.Settings;
using FastchessDesktop.ViewModels;
using Microsoft.UI.Xaml;

namespace FastchessDesktop.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    /// <summary>The root view model; created before any page is shown.</summary>
    public static ShellViewModel Shell { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        var dialogs = new WinUiDialogService(_window);
        var dispatcher = new DispatcherQueueDispatcher(_window.DispatcherQueue);
        Shell = new ShellViewModel(dialogs, dispatcher, new AppEnvironment(), SettingsStore.CreateDefault());

        _window.Closed += (_, _) =>
        {
            Shell.Tournament.StartCommand.Cancel();
            Shell.SaveSettings();
            Shell.Dispose();
        };
        _window.ShowShell();
        _window.Activate();
        await Shell.InitializeAsync();
    }
}
