using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.Core.Native;

/// <summary>
/// P/Invoke declarations for the cancellation, process, UCI, analysis and tournament parts of
/// native/include/fcd/fcd.h. Keep in sync with the header.
/// </summary>
internal static unsafe partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FcdProcessResult
    {
        public int ExitCode;
        public int Cancelled;
        public long DurationMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdUciLimit
    {
        public int Depth;
        public int MoveTimeMs;
        public long Nodes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdAnalysisSettings
    {
        public int Depth;
        public int MoveTimeMs;
    }

    [LibraryImport(Library, EntryPoint = "fcd_cancel_new")]
    public static partial FcdStatus CancelNew(out nint cancel);

    [LibraryImport(Library, EntryPoint = "fcd_cancel_request")]
    public static partial void CancelRequest(nint cancel);

    [LibraryImport(Library, EntryPoint = "fcd_cancel_free")]
    public static partial void CancelFree(nint cancel);

    [LibraryImport(Library, EntryPoint = "fcd_process_run", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus ProcessRun(string program, string argsJson, string? workingDirectory,
        delegate* unmanaged[Cdecl]<nint, int, byte*, void> onLine, nint user, nint cancel, out FcdProcessResult result);

    [LibraryImport(Library, EntryPoint = "fcd_uci_start", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus UciStart(string path, string? optionsJson, nint cancel, out nint engine);

    [LibraryImport(Library, EntryPoint = "fcd_uci_close")]
    public static partial void UciClose(nint engine);

    [LibraryImport(Library, EntryPoint = "fcd_uci_name")]
    public static partial FcdStatus UciName(UciHandle engine, out nint text);

    [LibraryImport(Library, EntryPoint = "fcd_uci_new_game")]
    public static partial FcdStatus UciNewGame(UciHandle engine, nint cancel);

    [LibraryImport(Library, EntryPoint = "fcd_uci_search", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus UciSearch(UciHandle engine, string? startFen, string uciMoves, in FcdUciLimit limit,
        nint cancel, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_uci_parse_info", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus UciParseInfo(string line, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_uci_analyze_game", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus UciAnalyzeGame(UciHandle engine, string? startFen, string uciMoves,
        in FcdAnalysisSettings settings, delegate* unmanaged[Cdecl]<nint, long, long, int> progress, nint user,
        nint cancel, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_tournament_run", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus TournamentRun(string fastchessPath, string workingDirectory, string settingsJson,
        delegate* unmanaged[Cdecl]<nint, int, byte*, void> onLine, delegate* unmanaged[Cdecl]<nint, byte*, void> onEvent,
        nint user, nint cancel, out nint outcomeJson);

    /// <summary>
    /// Maps the statuses of process, engine and tournament calls to the exceptions the rest of the
    /// application expects (as System.Diagnostics.Process and the former C# client threw them).
    /// </summary>
    public static void CheckProcess(FcdStatus status, CancellationToken token)
    {
        if (status == FcdStatus.Ok) return;
        var message = Marshal.PtrToStringUTF8(LastError()) ?? status.ToString();
        throw status switch
        {
            FcdStatus.Cancelled => new OperationCanceledException(token),
            FcdStatus.NotFound => new FileNotFoundException(message),
            FcdStatus.Timeout => new TimeoutException(message),
            FcdStatus.Process => new InvalidOperationException(message),
            _ => new FcdException(status, message),
        };
    }
}

/// <summary>A native cancel token (fcd_cancel) driven by a <see cref="CancellationToken"/>.</summary>
internal sealed class NativeCancellation : IDisposable
{
    private readonly CancellationTokenRegistration _registration;

    public NativeCancellation(CancellationToken token)
    {
        NativeMethods.Check(NativeMethods.CancelNew(out var handle));
        Handle = handle;
        _registration = token.Register(static state => NativeMethods.CancelRequest((nint)state!), handle);
    }

    public nint Handle { get; }

    public void Dispose()
    {
        // Unregistering waits for a running callback, so the token is not used after it is freed.
        _registration.Dispose();
        NativeMethods.CancelFree(Handle);
    }
}

/// <summary>Forwards native output-line and event callbacks to managed delegates.</summary>
internal static unsafe class CallbackBridge
{
    public sealed class State(Action<OutputLine>? onLine, Action<string>? onEvent)
    {
        public Action<OutputLine>? OnLine { get; } = onLine;
        public Action<string>? OnEvent { get; } = onEvent;
    }

    public static delegate* unmanaged[Cdecl]<nint, int, byte*, void> Line => &OnLine;

    public static delegate* unmanaged[Cdecl]<nint, byte*, void> Event => &OnEvent;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLine(nint user, int stream, byte* line)
    {
        try
        {
            var state = (State)GCHandle.FromIntPtr(user).Target!;
            state.OnLine?.Invoke(new OutputLine(stream == 1 ? OutputStream.StandardError : OutputStream.StandardOutput,
                Marshal.PtrToStringUTF8((nint)line) ?? ""));
        }
        catch
        {
            // Exceptions must not cross into native code.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(nint user, byte* json)
    {
        try
        {
            var state = (State)GCHandle.FromIntPtr(user).Target!;
            state.OnEvent?.Invoke(Marshal.PtrToStringUTF8((nint)json) ?? "");
        }
        catch
        {
            // Exceptions must not cross into native code.
        }
    }
}

internal sealed class UciHandle : SafeHandle
{
    public UciHandle() : base(0, ownsHandle: true) { }

    public UciHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.UciClose(handle);
        return true;
    }
}
