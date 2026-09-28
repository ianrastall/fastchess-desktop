using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FastchessDesktop.Core.Models;

namespace FastchessDesktop.Core.Native;

/// <summary>Progress reported by a long native operation. Total is 0 when unknown.</summary>
public readonly record struct NativeProgress(long Done, long Total);

/// <summary>Marshals a <see cref="GameQuery"/> into an fcd_query that stays valid until disposed.</summary>
internal sealed unsafe class QueryMarshaller : IDisposable
{
    private readonly nint _search;
    private readonly nint _orderBy;
    private readonly long[] _ids;
    private GCHandle _idsHandle;
    public NativeMethods.FcdQuery Value;

    public QueryMarshaller(GameQuery query)
    {
        _search = string.IsNullOrWhiteSpace(query.Search) ? 0 : Marshal.StringToCoTaskMemUTF8(query.Search.Trim());
        _orderBy = Marshal.StringToCoTaskMemUTF8(query.OrderByName);
        _ids = [.. query.Ids];
        _idsHandle = GCHandle.Alloc(_ids, GCHandleType.Pinned);
        Value = new NativeMethods.FcdQuery
        {
            Search = _search,
            OrderBy = _orderBy,
            Descending = query.Descending ? 1 : 0,
            Offset = query.Offset,
            Limit = query.Limit,
            Ids = _ids.Length > 0 ? (long*)_idsHandle.AddrOfPinnedObject() : null,
            IdCount = _ids.Length,
        };
    }

    public void Dispose()
    {
        if (_search != 0) Marshal.FreeCoTaskMem(_search);
        Marshal.FreeCoTaskMem(_orderBy);
        if (_idsHandle.IsAllocated) _idsHandle.Free();
    }
}

/// <summary>Bridges native progress callbacks to IProgress and CancellationToken.</summary>
internal static unsafe class ProgressBridge
{
    private sealed record State(IProgress<NativeProgress>? Progress, CancellationToken Token);

    public static delegate* unmanaged[Cdecl]<nint, long, long, int> Callback => &OnProgress;

    public static GCHandle Create(IProgress<NativeProgress>? progress, CancellationToken token) =>
        GCHandle.Alloc(new State(progress, token));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProgress(nint user, long done, long total)
    {
        try
        {
            var state = (State)GCHandle.FromIntPtr(user).Target!;
            state.Progress?.Report(new NativeProgress(done, total));
            return state.Token.IsCancellationRequested ? 1 : 0;
        }
        catch
        {
            // Exceptions must not cross into native code; cancel the operation instead.
            return 1;
        }
    }
}
