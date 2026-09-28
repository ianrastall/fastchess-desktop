using System.Text.Json;
using System.Text.Json.Serialization;
using FastchessDesktop.Core.Engines;
using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.Core.Settings;

/// <summary>Everything persisted between sessions.</summary>
public sealed record AppSettings
{
    public ToolPaths Tools { get; init; } = new();
    public string LastDatabasePath { get; init; } = "";
    public TournamentSettings Tournament { get; init; } = new();
    public bool ImportTournamentResults { get; init; } = true;
    public AnalysisSettings Analysis { get; init; } = new();
    public RatingSettings Ratings { get; init; } = new();
    public int PageSize { get; init; } = 250;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    /// <summary>%LOCALAPPDATA%\FastchessDesktop\settings.json</summary>
    public static SettingsStore CreateDefault() => new(System.IO.Path.Combine(DataDirectory, "settings.json"));

    public static string DataDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastchessDesktop");

    /// <summary>Returns saved settings, or defaults when the file is missing or unreadable.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically: a temporary file is written and then moved over the old one.</summary>
    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = Path + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        File.Move(temp, Path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
