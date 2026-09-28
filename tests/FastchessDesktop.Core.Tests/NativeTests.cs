using FastchessDesktop.Core.Models;
using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tests;

/// <summary>Exercises the C# wrapper over the real native library (built from native/).</summary>
public sealed class NativeTests : IDisposable
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("fcd-native");

    public void Dispose() => _dir.Delete(recursive: true);

    private GameDatabase OpenWithSample()
    {
        var db = GameDatabase.Open(Path.Combine(_dir.FullName, "games.fcdb"));
        var r = db.ImportPgn(Path.Combine(Data, "sample.pgn"), skipDuplicates: true);
        Assert.Equal(new ImportResult(4, 1, 1), r);
        return db;
    }

    [Fact]
    public void Library_reports_compatible_abi()
    {
        NativeLibraryInfo.EnsureCompatible();
        Assert.Equal("0.1.0", NativeLibraryInfo.Version);
    }

    [Fact]
    public void Query_sort_page_and_search()
    {
        using var db = OpenWithSample();
        Assert.Equal(4, db.Count(GameQuery.All));

        var page = db.Query(new GameQuery { OrderBy = GameSortColumn.White, Descending = true, Limit = 2 });
        Assert.Equal(["René", "Gamma"], page.Select(g => g.White));

        var search = db.Query(new GameQuery { Search = "ren" });
        var rene = Assert.Single(search);
        Assert.Equal("0-1", rene.Result);
        Assert.Equal(4, rene.PlyCount);
        Assert.Null(rene.WhiteElo);

        Assert.Equal(2, db.Count(GameQuery.ForIds([1, 2, 999])));
    }

    [Fact]
    public void Game_detail_tags_and_analysis()
    {
        using var db = OpenWithSample();
        var game = db.GetGame(1);
        Assert.Equal(["e2e4", "e7e5", "f1c4", "b8c6", "d1h5", "g8f6", "h5f7"], game.UciMoves);
        Assert.Equal("+0.30/12 0.10s", game.Comments[0]);
        Assert.Equal("00:00:05", game.Tags["GameDuration"]);
        Assert.Null(game.StartFen);

        db.SetTag(1, "WhiteElo", "2800");
        db.SetTag(1, "Annotator", "Me");
        var ex = Assert.Throws<FcdException>(() => db.SetTag(1, "Result", "2-0"));
        Assert.Equal(FcdStatus.Argument, ex.Status);

        db.SetAnalysis(1, "{\"format\":1}");
        game = db.GetGame(1);
        Assert.Equal(2800, game.WhiteElo);
        Assert.Equal("Me", game.Tags["Annotator"]);
        Assert.True(game.HasAnalysis);
        Assert.Equal(1, game.Analysis!.Value.GetProperty("format").GetInt32());

        Assert.Equal(FcdStatus.NotFound, Assert.Throws<FcdException>(() => db.GetGame(12345)).Status);
    }

    [Fact]
    public void Openings_fill_and_classify()
    {
        using var db = OpenWithSample();
        using var book = OpeningBook.LoadTsv(Path.Combine(Data, "lichess-openings.tsv"));
        Assert.Equal(3815, book.Count);

        var najdorf = book.Classify(["e2e4", "c7c5", "g1f3", "d7d6", "d2d4", "c5d4", "f3d4", "g8f6", "b1c3", "a7a6"]);
        Assert.Equal("B90", najdorf!.Eco);
        Assert.Null(book.Classify([]));

        var progress = new List<NativeProgress>();
        var updated = db.FillMissing(GameQuery.All, book, FillOptions.Opening | FillOptions.Result,
            new SyncProgress<NativeProgress>(progress.Add), TestContext.Current.CancellationToken);
        Assert.True(updated >= 3);
        Assert.NotEmpty(progress);
        var fools = db.GetGame(2);
        Assert.Equal("0-1", fools.Result);
        Assert.Equal("Barnes Opening", fools.Opening);

        var eco = Path.Combine(_dir.FullName, "eco.pgn");
        book.WriteEcoPgn(eco);
        Assert.Contains("[Opening \"Amar Opening\"]", File.ReadAllText(eco), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_rolls_back_import()
    {
        using var db = GameDatabase.Open(Path.Combine(_dir.FullName, "cancel.fcdb"));
        // The native side reports progress at the end of an import; a cancelled token must roll it back.
        var big = Path.Combine(_dir.FullName, "big.pgn");
        File.WriteAllText(big, string.Concat(Enumerable.Repeat(File.ReadAllText(Path.Combine(Data, "sample.pgn")) + "\n", 200)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => db.ImportPgn(big, false, null, cts.Token));
        Assert.Equal(0, db.Count(GameQuery.All));
    }

    [Fact]
    public void Exports_and_ratings()
    {
        using var db = OpenWithSample();
        var csv = Path.Combine(_dir.FullName, "ratings.csv");
        File.WriteAllText(csv, "\"#\",\"PLAYER\",\"RATING\",\"ERROR\",\"POINTS\",\"PLAYED\",\"(%)\"\n1,\"Alpha\",2612.4,35.1,3.5,4,87.5\n");
        var (players, written) = db.ApplyOrdoCsv(csv, fillElo: true, overwriteElo: false);
        Assert.Equal(1, players);
        Assert.Equal(3, written);
        Assert.Equal(2612.4, Assert.Single(db.GetRatings()).Rating);

        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var path = Path.Combine(_dir.FullName, "out." + format);
            Assert.Equal(4, db.Export(GameQuery.All, format, path));
            Assert.True(new FileInfo(path).Length > 100);
        }
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.FullName, "out.Json")));
        Assert.Equal(4, json.RootElement.GetProperty("games").GetArrayLength());
        System.Xml.Linq.XDocument.Load(Path.Combine(_dir.FullName, "out.Xml"));

        db.DeleteGames([1, 2]);
        Assert.Equal(2, db.Count(GameQuery.All));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
