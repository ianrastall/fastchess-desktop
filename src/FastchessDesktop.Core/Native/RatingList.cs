using System.Text.Json;
using FastchessDesktop.Core.Models;

namespace FastchessDesktop.Core.Native;

/// <summary>
/// An engine rating list read from an Ordo CSV file, such as the UCERL list. Immutable once loaded.
/// </summary>
public sealed class RatingList : IDisposable
{
    private RatingList(RatingListHandle handle) => Handle = handle;

    internal RatingListHandle Handle { get; }

    public long Count => NativeMethods.RatingsCount(Handle);

    /// <summary>Loads a CSV with PLAYER and RATING columns (and optionally PLAYED), as Ordo writes it.</summary>
    public static RatingList LoadCsv(string path)
    {
        NativeLibraryInfo.EnsureCompatible();
        NativeMethods.Check(NativeMethods.RatingsLoadCsv(path, out var raw));
        return new RatingList(new RatingListHandle(raw));
    }

    /// <summary>
    /// The rating for the name an engine reports, or an estimate when only older versions of it are
    /// listed; null when neither is listed.
    /// </summary>
    public EngineRating? Lookup(string engineName)
    {
        NativeMethods.Check(NativeMethods.RatingsLookup(Handle, engineName, out var raw));
        var json = NativeMethods.TakeString(raw);
        return json == "null" ? null : JsonSerializer.Deserialize(json, NativeJsonContext.Default.EngineRating);
    }

    public void Dispose() => Handle.Dispose();
}
