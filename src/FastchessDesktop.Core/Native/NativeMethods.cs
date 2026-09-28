using System.Runtime.InteropServices;

namespace FastchessDesktop.Core.Native;

/// <summary>P/Invoke declarations for native/include/fcd/fcd.h. Keep in sync with the header.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Library = "fcd_core";

    /// <summary>Must equal FCD_ABI_VERSION in fcd.h.</summary>
    public const int ExpectedAbiVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdQuery
    {
        public nint Search;
        public nint OrderBy;
        public int Descending;
        public long Offset;
        public long Limit;
        public long* Ids;
        public long IdCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdImportResult
    {
        public long Imported;
        public long Duplicates;
        public long Failed;
    }

    [LibraryImport(Library, EntryPoint = "fcd_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Library, EntryPoint = "fcd_version")]
    public static partial nint Version();

    [LibraryImport(Library, EntryPoint = "fcd_last_error")]
    public static partial nint LastError();

    [LibraryImport(Library, EntryPoint = "fcd_free")]
    public static partial void Free(nint p);

    [LibraryImport(Library, EntryPoint = "fcd_db_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus DbOpen(string path, out nint db);

    [LibraryImport(Library, EntryPoint = "fcd_db_close")]
    public static partial void DbClose(nint db);

    [LibraryImport(Library, EntryPoint = "fcd_db_import_pgn", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus DbImportPgn(
        DatabaseHandle db, string pgnPath, int skipDuplicates,
        delegate* unmanaged[Cdecl]<nint, long, long, int> progress, nint user, out FcdImportResult result);

    [LibraryImport(Library, EntryPoint = "fcd_db_count")]
    public static partial FcdStatus DbCount(DatabaseHandle db, FcdQuery* query, out long count);

    [LibraryImport(Library, EntryPoint = "fcd_db_query")]
    public static partial FcdStatus DbQuery(DatabaseHandle db, FcdQuery* query, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_db_get_game")]
    public static partial FcdStatus DbGetGame(DatabaseHandle db, long id, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_db_set_tag", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus DbSetTag(DatabaseHandle db, long id, string tag, string? value);

    [LibraryImport(Library, EntryPoint = "fcd_db_delete_games")]
    public static partial FcdStatus DbDeleteGames(DatabaseHandle db, long* ids, long idCount);

    [LibraryImport(Library, EntryPoint = "fcd_db_set_analysis", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus DbSetAnalysis(DatabaseHandle db, long id, string? analysisJson);

    [LibraryImport(Library, EntryPoint = "fcd_db_fill_missing")]
    public static partial FcdStatus DbFillMissing(
        DatabaseHandle db, FcdQuery* query, nint book, uint flags,
        delegate* unmanaged[Cdecl]<nint, long, long, int> progress, nint user, out long updated);

    [LibraryImport(Library, EntryPoint = "fcd_db_apply_ordo_csv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus DbApplyOrdoCsv(
        DatabaseHandle db, string csvPath, int fillElo, int overwriteElo, out long players, out long eloValuesWritten);

    [LibraryImport(Library, EntryPoint = "fcd_db_get_ratings")]
    public static partial FcdStatus DbGetRatings(DatabaseHandle db, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_db_export", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus DbExport(DatabaseHandle db, FcdQuery* query, int format, string outPath, out long games);

    [LibraryImport(Library, EntryPoint = "fcd_openings_load_tsv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus OpeningsLoadTsv(string tsvPath, out nint book);

    [LibraryImport(Library, EntryPoint = "fcd_openings_free")]
    public static partial void OpeningsFree(nint book);

    [LibraryImport(Library, EntryPoint = "fcd_openings_count")]
    public static partial long OpeningsCount(OpeningBookHandle book);

    [LibraryImport(Library, EntryPoint = "fcd_openings_write_eco_pgn", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus OpeningsWriteEcoPgn(OpeningBookHandle book, string outPath);

    [LibraryImport(Library, EntryPoint = "fcd_openings_classify_uci", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus OpeningsClassifyUci(OpeningBookHandle book, string uciMoves, out nint json);

    /// <summary>Copies and frees a library-owned UTF-8 string.</summary>
    public static string TakeString(nint p)
    {
        try
        {
            return Marshal.PtrToStringUTF8(p) ?? "";
        }
        finally
        {
            Free(p);
        }
    }

    public static void Check(FcdStatus status)
    {
        if (status != FcdStatus.Ok)
            throw new FcdException(status, Marshal.PtrToStringUTF8(LastError()) ?? "");
    }
}

public enum FcdStatus
{
    Ok = 0,
    Argument = 1,
    Io = 2,
    Database = 3,
    Parse = 4,
    NotFound = 5,
    Cancelled = 6,
    Internal = 99,
}

/// <summary>An error reported by the native core.</summary>
public sealed class FcdException(FcdStatus status, string message)
    : Exception(string.IsNullOrEmpty(message) ? status.ToString() : message)
{
    public FcdStatus Status { get; } = status;
}

internal sealed class DatabaseHandle : SafeHandle
{
    public DatabaseHandle() : base(0, ownsHandle: true) { }

    public DatabaseHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.DbClose(handle);
        return true;
    }
}

internal sealed class OpeningBookHandle : SafeHandle
{
    public OpeningBookHandle() : base(0, ownsHandle: true) { }

    public OpeningBookHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.OpeningsFree(handle);
        return true;
    }
}
