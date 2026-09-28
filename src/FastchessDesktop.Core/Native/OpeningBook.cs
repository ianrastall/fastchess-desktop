using System.Text.Json;
using FastchessDesktop.Core.Models;

namespace FastchessDesktop.Core.Native;

/// <summary>The Lichess chess-openings index, keyed by complete EPD. Immutable once loaded.</summary>
public sealed class OpeningBook : IDisposable
{
    private OpeningBook(OpeningBookHandle handle) => Handle = handle;

    internal OpeningBookHandle Handle { get; }

    public long Count => NativeMethods.OpeningsCount(Handle);

    /// <summary>Loads and validates a five-column eco/name/pgn/uci/epd TSV file.</summary>
    public static OpeningBook LoadTsv(string path)
    {
        NativeLibraryInfo.EnsureCompatible();
        NativeMethods.Check(NativeMethods.OpeningsLoadTsv(path, out var raw));
        return new OpeningBook(new OpeningBookHandle(raw));
    }

    /// <summary>Writes the book as a pgn-extract ECO file for use with -e.</summary>
    public void WriteEcoPgn(string path) => NativeMethods.Check(NativeMethods.OpeningsWriteEcoPgn(Handle, path));

    /// <summary>Classifies UCI moves from the standard start; null when no named position is reached.</summary>
    public OpeningInfo? Classify(IEnumerable<string> uciMoves)
    {
        NativeMethods.Check(NativeMethods.OpeningsClassifyUci(Handle, string.Join(' ', uciMoves), out var raw));
        var json = NativeMethods.TakeString(raw);
        return json == "null" ? null : JsonSerializer.Deserialize(json, NativeJsonContext.Default.OpeningInfo);
    }

    public void Dispose() => Handle.Dispose();
}
