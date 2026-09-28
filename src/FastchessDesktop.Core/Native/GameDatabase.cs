using System.Runtime.InteropServices;
using System.Text.Json;
using FastchessDesktop.Core.Models;

namespace FastchessDesktop.Core.Native;

/// <summary>
/// A game database file managed by the native core. Calls are serialized with a lock,
/// so an instance may be used from background threads. Long operations block the
/// calling thread; run them with Task.Run from UI code.
/// </summary>
public sealed unsafe class GameDatabase : IDisposable
{
    private readonly DatabaseHandle _handle;
    private readonly Lock _lock = new();

    private GameDatabase(DatabaseHandle handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Opens or creates a database file.</summary>
    public static GameDatabase Open(string path)
    {
        NativeLibraryInfo.EnsureCompatible();
        NativeMethods.Check(NativeMethods.DbOpen(path, out var raw));
        return new GameDatabase(new DatabaseHandle(raw), path);
    }

    public ImportResult ImportPgn(string pgnPath, bool skipDuplicates,
        IProgress<NativeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var state = ProgressBridge.Create(progress, cancellationToken);
        try
        {
            lock (_lock)
            {
                var status = NativeMethods.DbImportPgn(_handle, pgnPath, skipDuplicates ? 1 : 0,
                    ProgressBridge.Callback, GCHandle.ToIntPtr(state), out var r);
                ThrowIfCancelled(status, cancellationToken);
                NativeMethods.Check(status);
                return new ImportResult(r.Imported, r.Duplicates, r.Failed);
            }
        }
        finally
        {
            state.Free();
        }
    }

    public long Count(GameQuery query)
    {
        using var q = new QueryMarshaller(query);
        lock (_lock)
        {
            fixed (NativeMethods.FcdQuery* p = &q.Value)
            {
                NativeMethods.Check(NativeMethods.DbCount(_handle, p, out var count));
                return count;
            }
        }
    }

    public IReadOnlyList<GameSummary> Query(GameQuery query)
    {
        using var q = new QueryMarshaller(query);
        string json;
        lock (_lock)
        {
            fixed (NativeMethods.FcdQuery* p = &q.Value)
            {
                NativeMethods.Check(NativeMethods.DbQuery(_handle, p, out var raw));
                json = NativeMethods.TakeString(raw);
            }
        }
        return JsonSerializer.Deserialize(json, NativeJsonContext.Default.ListGameSummary) ?? [];
    }

    public GameDetail GetGame(long id)
    {
        string json;
        lock (_lock)
        {
            NativeMethods.Check(NativeMethods.DbGetGame(_handle, id, out var raw));
            json = NativeMethods.TakeString(raw);
        }
        return JsonSerializer.Deserialize(json, NativeJsonContext.Default.GameDetail)
            ?? throw new FcdException(FcdStatus.Internal, "empty game document");
    }

    /// <summary>Sets or clears (null or empty value) a PGN tag.</summary>
    public void SetTag(long id, string tag, string? value)
    {
        lock (_lock) NativeMethods.Check(NativeMethods.DbSetTag(_handle, id, tag, value));
    }

    public void DeleteGames(IReadOnlyCollection<long> ids)
    {
        var array = ids.ToArray();
        lock (_lock)
        {
            fixed (long* p = array)
                NativeMethods.Check(NativeMethods.DbDeleteGames(_handle, p, array.Length));
        }
    }

    /// <summary>Stores analysis JSON for a game, or clears it when json is null.</summary>
    public void SetAnalysis(long id, string? json)
    {
        lock (_lock) NativeMethods.Check(NativeMethods.DbSetAnalysis(_handle, id, json));
    }

    public long FillMissing(GameQuery query, OpeningBook? book, FillOptions options,
        IProgress<NativeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var q = new QueryMarshaller(query);
        var state = ProgressBridge.Create(progress, cancellationToken);
        var addedRef = false;
        try
        {
            book?.Handle.DangerousAddRef(ref addedRef);
            var bookPtr = book is null ? 0 : book.Handle.DangerousGetHandle();
            lock (_lock)
            {
                fixed (NativeMethods.FcdQuery* p = &q.Value)
                {
                    var status = NativeMethods.DbFillMissing(_handle, p, bookPtr, (uint)options,
                        ProgressBridge.Callback, GCHandle.ToIntPtr(state), out var updated);
                    ThrowIfCancelled(status, cancellationToken);
                    NativeMethods.Check(status);
                    return updated;
                }
            }
        }
        finally
        {
            if (addedRef) book!.Handle.DangerousRelease();
            state.Free();
        }
    }

    /// <summary>Imports an Ordo CSV rating list. Returns players read and Elo values written.</summary>
    public (long Players, long EloValuesWritten) ApplyOrdoCsv(string csvPath, bool fillElo, bool overwriteElo)
    {
        lock (_lock)
        {
            NativeMethods.Check(NativeMethods.DbApplyOrdoCsv(_handle, csvPath, fillElo ? 1 : 0, overwriteElo ? 1 : 0,
                out var players, out var written));
            return (players, written);
        }
    }

    public IReadOnlyList<PlayerRating> GetRatings()
    {
        string json;
        lock (_lock)
        {
            NativeMethods.Check(NativeMethods.DbGetRatings(_handle, out var raw));
            json = NativeMethods.TakeString(raw);
        }
        return JsonSerializer.Deserialize(json, NativeJsonContext.Default.ListPlayerRating) ?? [];
    }

    /// <summary>Exports the selected games. Returns the number of games written.</summary>
    public long Export(GameQuery query, ExportFormat format, string outPath)
    {
        using var q = new QueryMarshaller(query);
        lock (_lock)
        {
            fixed (NativeMethods.FcdQuery* p = &q.Value)
            {
                NativeMethods.Check(NativeMethods.DbExport(_handle, p, (int)format, outPath, out var games));
                return games;
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    private static void ThrowIfCancelled(FcdStatus status, CancellationToken token)
    {
        if (status == FcdStatus.Cancelled) throw new OperationCanceledException(token);
    }
}
