using CommunityToolkit.Mvvm.ComponentModel;
using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.ViewModels;

/// <summary>One engine entry. UCI options are edited as "Name=Value" lines.</summary>
public sealed partial class EngineViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Command { get; set; } = "";

    [ObservableProperty] public partial string Arguments { get; set; } = "";
    [ObservableProperty] public partial string WorkingDirectory { get; set; } = "";
    [ObservableProperty] public partial string OptionsText { get; set; } = "";

    /// <summary>Result of the last name detection, shown under the name field.</summary>
    [ObservableProperty] public partial string Status { get; set; } = "";

    public string DisplayName
    {
        get
        {
            var name = FastchessCommandBuilder.EngineName(ToSettings());
            return name.Length > 0 ? name : "(new engine)";
        }
    }

    public static EngineViewModel From(EngineSettings s) => new()
    {
        Name = s.Name,
        Command = s.Command,
        Arguments = s.Arguments,
        WorkingDirectory = s.WorkingDirectory,
        OptionsText = string.Join(Environment.NewLine, s.Options.Select(o => $"{o.Name}={o.Value}")),
    };

    public EngineSettings ToSettings() => new()
    {
        Name = Name.Trim(),
        Command = Command.Trim(),
        Arguments = Arguments.Trim(),
        WorkingDirectory = WorkingDirectory.Trim(),
        Options = ParseOptions(OptionsText),
    };

    /// <summary>Parses "Name=Value" lines. Blank lines and lines starting with # are ignored.</summary>
    public static IReadOnlyList<EngineOption> ParseOptions(string text)
    {
        var options = new List<EngineOption>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            options.Add(eq < 0 ? new EngineOption(line, "") : new EngineOption(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return options;
    }
}
