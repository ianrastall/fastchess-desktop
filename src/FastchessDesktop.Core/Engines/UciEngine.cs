using System.Text.Json;
using System.Text.Json.Serialization;
using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Engines;

/// <summary>Score reported by an engine, from the side to move's point of view.</summary>
public readonly record struct UciScore(int? Centipawns, int? Mate)
{
    public UciScore Negate() => new(-Centipawns, -Mate);
}

public sealed record UciSearchResult(UciScore Score, int Depth, string BestMove, IReadOnlyList<string> Pv);

/// <summary>Search limit for one position.</summary>
public sealed record UciLimit(int Depth = 0, int MoveTimeMs = 0, long Nodes = 0);

/// <summary>
/// UCI client (one engine process, one search at a time), implemented in fcd_core. Calls block a
/// thread-pool thread while the engine works; do not call it concurrently.
/// </summary>
public sealed class UciEngine : IAsyncDisposable
{
    private readonly UciHandle _handle;

    private UciEngine(UciHandle handle, string name)
    {
        _handle = handle;
        Name = name;
    }

    /// <summary>The name the engine reported with "id name" (empty when it sent none).</summary>
    public string Name { get; }

    /// <summary>
    /// Starts the engine, completes the uci/isready handshake and applies options. Throws
    /// FileNotFoundException, TimeoutException (no answer) or InvalidOperationException (the engine exited).
    /// </summary>
    public static Task<UciEngine> StartAsync(string path, IReadOnlyDictionary<string, string> options,
        CancellationToken ct) => Task.Run(() =>
    {
        var optionsJson = JsonSerializer.Serialize(options.ToDictionary(kv => kv.Key, kv => kv.Value),
            UciJsonContext.Default.DictionaryStringString);
        using var cancel = new NativeCancellation(ct);
        NativeMethods.CheckProcess(NativeMethods.UciStart(path, optionsJson, cancel.Handle, out var raw), ct);
        var handle = new UciHandle(raw);
        var status = NativeMethods.UciName(handle, out var name);
        return new UciEngine(handle, NativeMethods.TakeChecked(status, name));
    }, CancellationToken.None);

    public Task NewGameAsync(CancellationToken ct) => Task.Run(() =>
    {
        using var cancel = new NativeCancellation(ct);
        NativeMethods.CheckProcess(NativeMethods.UciNewGame(_handle, cancel.Handle), ct);
    }, CancellationToken.None);

    /// <summary>Searches the position reached from startFen (null for startpos) after the given UCI moves.</summary>
    public Task<UciSearchResult> SearchAsync(string? startFen, IEnumerable<string> moves, UciLimit limit,
        CancellationToken ct)
    {
        var moveList = string.Join(' ', moves);
        return Task.Run(() =>
        {
            using var cancel = new NativeCancellation(ct);
            var nativeLimit = new NativeMethods.FcdUciLimit { Depth = limit.Depth, MoveTimeMs = limit.MoveTimeMs, Nodes = limit.Nodes };
            var status = NativeMethods.UciSearch(_handle, startFen, moveList, nativeLimit, cancel.Handle, out var json);
            NativeMethods.CheckProcess(status, ct);
            var r = JsonSerializer.Deserialize(NativeMethods.TakeString(json), UciJsonContext.Default.UciSearchDto)!;
            return new UciSearchResult(new UciScore(r.Cp, r.Mate), r.Depth, r.BestMove ?? "", r.Pv ?? []);
        }, CancellationToken.None);
    }

    /// <summary>Parses the score, depth and pv of a UCI info line. Ignores bound and multipv>1 lines.</summary>
    public static bool TryParseInfo(string line, out UciScore score, out int depth, out IReadOnlyList<string> pv)
    {
        var status = NativeMethods.UciParseInfo(line, out var raw);
        var json = NativeMethods.TakeChecked(status, raw);
        var info = json == "null" ? null : JsonSerializer.Deserialize(json, UciJsonContext.Default.UciSearchDto);
        score = info is null ? default : new UciScore(info.Cp, info.Mate);
        depth = info?.Depth ?? 0;
        pv = info?.Pv ?? [];
        return info is not null;
    }

    internal UciHandle Handle => _handle;

    /// <summary>Sends quit, waits up to two seconds, then stops the engine process.</summary>
    public ValueTask DisposeAsync() => new(Task.Run(_handle.Dispose));
}

internal sealed record UciSearchDto(int? Cp, int? Mate, int Depth, string? BestMove, List<string>? Pv);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(UciSearchDto))]
internal sealed partial class UciJsonContext : JsonSerializerContext;
