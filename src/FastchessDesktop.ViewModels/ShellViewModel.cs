using CommunityToolkit.Mvvm.ComponentModel;
using FastchessDesktop.Core.Settings;
using FastchessDesktop.ViewModels.Services;

namespace FastchessDesktop.ViewModels;

/// <summary>Root view model: owns the pages and persists settings.</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;

    public ShellViewModel(IDialogService dialogs, IUiDispatcher dispatcher, IAppEnvironment environment, SettingsStore store)
    {
        _store = store;
        Settings = new SettingsViewModel(dialogs, environment);
        Database = new DatabaseViewModel(dialogs, dispatcher, environment, Settings);
        Tournament = new TournamentViewModel(dialogs, dispatcher, environment, Settings, Database);
    }

    public SettingsViewModel Settings { get; }
    public DatabaseViewModel Database { get; }
    public TournamentViewModel Tournament { get; }

    /// <summary>
    /// Loads saved settings and reopens the last database if it still exists; otherwise opens the
    /// default database, so there is always somewhere for games to go.
    /// </summary>
    public async Task InitializeAsync()
    {
        var saved = _store.Load();
        Settings.Load(saved.Tools, saved.Analysis, saved.Ratings, saved.PageSize);
        Tournament.Load(saved.Tournament, saved.ImportTournamentResults);
        if (saved.LastDatabasePath.Length > 0 && File.Exists(saved.LastDatabasePath))
            await Database.OpenAsync(saved.LastDatabasePath);
        else
            await Database.EnsureOpenAsync();
    }

    public AppSettings CollectSettings() => new()
    {
        Tools = Settings.ToToolPaths(),
        LastDatabasePath = Database.DatabasePath,
        Tournament = Tournament.ToSettings(),
        ImportTournamentResults = Tournament.ImportResults,
        Analysis = Settings.ToAnalysisSettings(),
        Ratings = Settings.ToRatingSettings(),
        PageSize = Database.PageSize,
    };

    public void SaveSettings() => _store.Save(CollectSettings());

    public void Dispose()
    {
        Tournament.Dispose();
        Database.Dispose();
    }
}
