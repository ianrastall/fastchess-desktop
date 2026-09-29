using FastchessDesktop.App.Services;
using FastchessDesktop.Core.Settings;
using FastchessDesktop.ViewModels;
using Microsoft.UI.Xaml;

namespace FastchessDesktop.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

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

    /// <summary>
    /// An exception that reaches the dispatcher (for example from a command) would end the app without
    /// a word. It is written to errors.log in the data folder and to the tournament log instead.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var log = Path.Combine(SettingsStore.DataDirectory, "errors.log");
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
            // The log line below still reports the error.
        }
        Shell?.Tournament.Log.Error($"Unexpected error: {e.Message} Details: {log}");
    }
}
