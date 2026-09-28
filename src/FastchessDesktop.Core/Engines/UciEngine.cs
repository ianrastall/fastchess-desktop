using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace FastchessDesktop.Core.Engines;

/// <summary>Score reported by an engine, from the side to move's point of view.</summary>
public readonly record struct UciScore(int? Centipawns, int? Mate)
{
    public UciScore Negate() => new(-Centipawns, -Mate);
}

public sealed record UciSearchResult(UciScore Score, int Depth, string BestMove, IReadOnlyList<string> Pv);

/// <summary>Search limit for one position.</summary>
public sealed record UciLimit(int Depth = 0, int MoveTimeMs = 0, long Nodes = 0)
{
    public string ToGoCommand()
    {
        var parts = new List<string> { "go" };
        if (Depth > 0) parts.Add("depth " + Depth.ToString(CultureInfo.InvariantCulture));
        if (MoveTimeMs > 0) parts.Add("movetime " + MoveTimeMs.ToString(CultureInfo.InvariantCulture));
        if (Nodes > 0) parts.Add("nodes " + Nodes.ToString(CultureInfo.InvariantCulture));
        if (parts.Count == 1) parts.Add("depth 12");
        return string.Join(' ', parts);
    }
}

/// <summary>Minimal UCI client: one engine process, one search at a time.</summary>
public sealed class UciEngine : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Task _reader;

    private UciEngine(Process process)
    {
        _process = process;
        _reader = Task.Run(ReadLoopAsync);
    }

    public string Name { get; private set; } = "";

    /// <summary>Starts the engine, completes the uci/isready handshake and applies options.</summary>
    public static async Task<UciEngine> StartAsync(string path, IReadOnlyDictionary<string, string> options,
        CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Engine not found: {path}", path);
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {path}");
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        var engine = new UciEngine(process);
        try
        {
            await engine.SendAsync("uci").ConfigureAwait(false);
            await engine.WaitForAsync(l =>
            {
                if (l.StartsWith("id name ", StringComparison.Ordinal)) engine.Name = l[8..].Trim();
                return l == "uciok";
            }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            foreach (var (name, value) in options)
                await engine.SendAsync($"setoption name {name} value {value}").ConfigureAwait(false);
            await engine.SyncAsync(ct).ConfigureAwait(false);
            return engine;
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task NewGameAsync(CancellationToken ct)
    {
        await SendAsync("ucinewgame").ConfigureAwait(false);
        await SyncAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Searches the position reached from startFen (null for startpos) after the given UCI moves.</summary>
    public async Task<UciSearchResult> SearchAsync(string? startFen, IEnumerable<string> moves, UciLimit limit,
        CancellationToken ct)
    {
        var moveList = string.Join(' ', moves);
        var position = startFen is null ? "position startpos" : "position fen " + startFen;
        if (moveList.Length > 0) position += " moves " + moveList;
        await SendAsync(position).ConfigureAwait(false);
        await SendAsync(limit.ToGoCommand()).ConfigureAwait(false);

        var score = new UciScore(null, null);
        var depth = 0;
        IReadOnlyList<string> pv = [];
        try
        {
            while (true)
            {
                var line = await _lines.Reader.ReadAsync(ct).ConfigureAwait(false);
                if (line.StartsWith("info ", StringComparison.Ordinal))
                {
                    if (TryParseInfo(line, out var s, out var d, out var p))
                    {
                        score = s;
                        depth = d;
                        if (p.Count > 0) pv = p;
                    }
                }
                else if (line.StartsWith("bestmove", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return new UciSearchResult(score, depth, parts.Length > 1 ? parts[1] : "", pv);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop the search so the engine is usable (or at least quiet) afterwards.
            await SendAsync("stop").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Parses the score, depth and pv of a UCI info line. Ignores bound and multipv>1 lines.</summary>
    public static bool TryParseInfo(string line, out UciScore score, out int depth, out IReadOnlyList<string> pv)
    {
        score = default;
        depth = 0;
        pv = [];
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hasScore = false;
        for (var i = 1; i < t.Length; i++)
        {
            switch (t[i])
            {
                case "depth" when i + 1 < t.Length:
                    int.TryParse(t[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out depth);
                    break;
                case "multipv" when i + 1 < t.Length:
                    if (t[++i] != "1") return false;
                    break;
                case "lowerbound" or "upperbound":
                    return false;
                case "score" when i + 2 < t.Length:
                    var kind = t[++i];
                    if (!int.TryParse(t[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return false;
                    score = kind == "mate" ? new UciScore(null, v) : new UciScore(v, null);
                    hasScore = kind is "cp" or "mate";
                    break;
                case "pv":
                    pv = t[(i + 1)..];
                    i = t.Length;
                    break;
                case "string":
                    i = t.Length;
                    break;
            }
        }
        return hasScore;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await SendAsync("quit").ConfigureAwait(false);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // The engine already went away.
        }
        await _reader.ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task SendAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        await SendAsync("isready").ConfigureAwait(false);
        await WaitForAsync(l => l == "readyok", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    private async Task WaitForAsync(Func<string, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!predicate(await _lines.Reader.ReadAsync(cts.Token).ConfigureAwait(false)))
            {
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Engine did not respond within {timeout.TotalSeconds:0} s.");
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("Engine process exited unexpectedly.");
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                await _lines.Writer.WriteAsync(line.Trim()).ConfigureAwait(false);
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }
}
