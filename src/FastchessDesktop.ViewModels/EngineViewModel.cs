using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FastchessDesktop.Core.Models;
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

    /// <summary>The engine's entry in the rating list, looked up by its name; null when it is not listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingText), nameof(RatingDetail), nameof(Tier))]
    public partial EngineRating? Rating { get; set; }

    /// <summary>Why there is no rating: no list loaded, or the engine is not in it. Shown when Rating is null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingDetail))]
    public partial string RatingNote { get; set; } = "";

    /// <summary>The rating for the engine list: "3823", or "~3833" for an estimate; empty when not rated.</summary>
    public string RatingText => Rating is not { } r ? ""
        : (r.Estimated ? "~" : "") + r.Rating.ToString("0", CultureInfo.InvariantCulture);

    public string RatingDetail
    {
        get
        {
            if (Rating is not { } r) return RatingNote;
            var few = r.Games < FewGames ? ", few games" : "";
            return r.Estimated
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Rating {r.Rating:0.0} estimated: {r.Player} is rated {r.BaseRating:0.0} ({r.Games} games{few}), plus 10 for a newer version.")
                : string.Create(CultureInfo.InvariantCulture, $"Rating {r.Rating:0.0} as {r.Player} ({r.Games} games{few}).");
        }
    }

    /// <summary>Ratings from fewer games than this are marked as uncertain.</summary>
    public const int FewGames = 100;

    /// <summary>Rating tier for the engine list colors: see <see cref="TierOf"/>.</summary>
    public int Tier => TierOf(Rating?.Rating);

    /// <summary>
    /// Tiers of 200 Elo: 1 is 3700 and above, 2 is 3500 to 3699, 3 is 3300 to 3499, 4 is 3100 to 3299,
    /// 5 is 2900 to 3099, 6 is below 2900, and 0 is not rated.
    /// </summary>
    public static int TierOf(double? rating) => rating switch
    {
        null => 0,
        >= 3700 => 1,
        >= 3500 => 2,
        >= 3300 => 3,
        >= 3100 => 4,
        >= 2900 => 5,
        _ => 6,
    };

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
