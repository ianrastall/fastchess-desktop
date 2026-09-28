namespace FastchessDesktop.ViewModels.Services;

/// <summary>A file type choice, e.g. new("PGN games", ".pgn").</summary>
public sealed record FileFilter(string Description, params string[] Extensions);

/// <summary>Dialogs and pickers, implemented by the UI layer.</summary>
public interface IDialogService
{
    Task<string?> PickOpenFileAsync(IReadOnlyList<FileFilter> filters);

    Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<FileFilter> filters);

    Task<string?> PickFolderAsync();

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    Task ShowMessageAsync(string title, string message);
}

/// <summary>Runs an action on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Where the application lives and where it keeps its data.</summary>
public interface IAppEnvironment
{
    /// <summary>Directory of the executable; bundled tools are found under it.</summary>
    string AppDirectory { get; }

    /// <summary>Per-user writable directory for settings, runs and scratch files.</summary>
    string DataDirectory { get; }
}

public static class FileFilters
{
    public static readonly IReadOnlyList<FileFilter> Pgn = [new("PGN games", ".pgn")];
    public static readonly IReadOnlyList<FileFilter> Database = [new("Game database", ".fcdb")];
    public static readonly IReadOnlyList<FileFilter> Executable = [new("Programs", ".exe")];
    public static readonly IReadOnlyList<FileFilter> Openings = [new("Opening books", ".epd", ".pgn")];
    public static readonly IReadOnlyList<FileFilter> Tsv = [new("Tab-separated values", ".tsv")];
    public static readonly IReadOnlyList<FileFilter> Any = [new("All files", "*")];
}
