using System.Text.Json;
using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

/// <summary>A pairing; <see cref="Black"/> is null for a bye. White is listed first to fastchess.</summary>
public sealed record Pairing(string White, string? Black);

/// <summary>Pairing rules for the formats fastchess does not provide itself, implemented in fcd_core.</summary>
public static class TournamentFormats
{
    public static bool IsRunByFastchess(TournamentType type) =>
        type is TournamentType.RoundRobin or TournamentType.Gauntlet;

    /// <summary>
    /// Standard bracket order for a power-of-two field: 1, N, N/2, N/2+1, ... so that seeds 1 and 2
    /// can only meet in the final (for 8: 1 8 4 5 2 7 3 6).
    /// </summary>
    public static IReadOnlyList<int> BracketOrder(int size)
    {
        var status = NativeMethods.PairingBracketOrder(size, out var json);
        if (status == FcdStatus.Argument)
            throw new ArgumentOutOfRangeException(nameof(size), "size must be a power of two >= 2");
        return JsonSerializer.Deserialize(NativeMethods.TakeChecked(status, json), ToolJsonContext.Default.ListInt32) ?? [];
    }

    /// <summary>
    /// First knockout round for players in seed order. The bracket is padded to a power of two;
    /// the top seeds receive the byes.
    /// </summary>
    public static IReadOnlyList<Pairing> KnockoutFirstRound(IReadOnlyList<string> seeded)
    {
        var status = NativeMethods.PairingKnockoutFirstRound(NativeMethods.ToJson(seeded), out var json);
        NativeMethods.CheckArgument(status);
        return Pairings(NativeMethods.TakeChecked(status, json));
    }

    /// <summary>Next knockout round: winners of adjacent matches meet.</summary>
    public static IReadOnlyList<Pairing> KnockoutNextRound(IReadOnlyList<string> winnersInBracketOrder)
    {
        var status = NativeMethods.PairingKnockoutNextRound(NativeMethods.ToJson(winnersInBracketOrder), out var json);
        return Pairings(NativeMethods.TakeChecked(status, json));
    }

    /// <summary>
    /// One Swiss round. Players are ranked by score (then by seed) and paired top-down with the
    /// nearest opponent they have not met; backtracking resolves dead ends, and rematches are
    /// allowed only when no rematch-free pairing exists. With an odd count, the lowest-ranked
    /// player without a bye sits out. Within a pair, the player with fewer games as White
    /// (then the higher-ranked one) is listed first.
    /// </summary>
    public static (IReadOnlyList<Pairing> Pairs, string? Bye) SwissRound(
        IReadOnlyList<string> seeded,
        IReadOnlyDictionary<string, double> scores,
        IReadOnlyDictionary<string, HashSet<string>> opponents,
        IReadOnlySet<string> hadBye,
        IReadOnlyDictionary<string, int> whiteCounts)
    {
        var state = new SwissStateDto(seeded, scores,
            opponents.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), [.. hadBye], whiteCounts);
        var status = NativeMethods.PairingSwissRound(JsonSerializer.Serialize(state, ToolJsonContext.Default.SwissStateDto),
            out var json);
        var round = JsonSerializer.Deserialize(NativeMethods.TakeChecked(status, json), ToolJsonContext.Default.SwissRoundDto)!;
        return ([.. round.Pairs.Select(p => new Pairing(p.White, p.Black))], round.Bye);
    }

    private static List<Pairing> Pairings(string json) =>
        [.. (JsonSerializer.Deserialize(json, ToolJsonContext.Default.ListPairingDto) ?? []).Select(p => new Pairing(p.White, p.Black))];
}
