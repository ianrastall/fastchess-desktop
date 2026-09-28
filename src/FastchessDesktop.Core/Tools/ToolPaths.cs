namespace FastchessDesktop.Core.Tools;

/// <summary>Locations of the external programs and data files. Empty means not configured.</summary>
public sealed record ToolPaths
{
    public string Fastchess { get; init; } = "";
    public string Stockfish { get; init; } = "";
    public string Ordo { get; init; } = "";
    public string Ordoprep { get; init; } = "";
    public string PgnExtract { get; init; } = "";
    public string OpeningsTsv { get; init; } = "";

    /// <summary>
    /// Fills empty entries from the application's bundled layout:
    /// tools\fastchess\fastchess.exe, tools\ordo\ordo-win64.exe, tools\ordoprep\ordoprep-win64.exe,
    /// tools\pgn-extract\pgn-extract.exe, tools\stockfish\*.exe and data\lichess-openings.tsv.
    /// </summary>
    public ToolPaths WithDefaults(string appDirectory)
    {
        string Find(string current, string relativeDir, params string[] names)
        {
            if (!string.IsNullOrWhiteSpace(current)) return current;
            var dir = Path.Combine(appDirectory, relativeDir);
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            if (Directory.Exists(dir) && names.Any(n => n.Contains('*')))
            {
                var match = Directory.EnumerateFiles(dir, names.First(n => n.Contains('*')), SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (match is not null) return match;
            }
            return "";
        }

        return this with
        {
            Fastchess = Find(Fastchess, Path.Combine("tools", "fastchess"), "fastchess.exe", "fastchess"),
            Stockfish = Find(Stockfish, Path.Combine("tools", "stockfish"), "stockfish.exe", "stockfish", "stockfish*.exe"),
            Ordo = Find(Ordo, Path.Combine("tools", "ordo"), "ordo-win64.exe", "ordo"),
            Ordoprep = Find(Ordoprep, Path.Combine("tools", "ordoprep"), "ordoprep-win64.exe", "ordoprep"),
            PgnExtract = Find(PgnExtract, Path.Combine("tools", "pgn-extract"), "pgn-extract.exe", "pgn-extract"),
            OpeningsTsv = Find(OpeningsTsv, "data", "lichess-openings.tsv"),
        };
    }
}
