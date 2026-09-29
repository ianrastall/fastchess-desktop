namespace FastchessDesktop.Core.Tools;

/// <summary>A pairing; <see cref="Black"/> is null for a bye. White is listed first to fastchess.</summary>
public sealed record Pairing(string White, string? Black);

/// <summary>Pairing rules for the formats fastchess does not provide itself.</summary>
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
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be a power of two >= 2");
        var seeds = new List<int> { 1 };
        while (seeds.Count < size)
        {
            var next = new List<int>(seeds.Count * 2);
            var sum = seeds.Count * 2 + 1;
            foreach (var s in seeds)
            {
                next.Add(s);
                next.Add(sum - s);
            }
            seeds = next;
        }
        return seeds;
    }

    /// <summary>
    /// First knockout round for players in seed order. The bracket is padded to a power of two;
    /// the top seeds receive the byes.
    /// </summary>
    public static IReadOnlyList<Pairing> KnockoutFirstRound(IReadOnlyList<string> seeded)
    {
        if (seeded.Count < 2) throw new ArgumentException("A knockout needs at least two players.", nameof(seeded));
        var size = 2;
        while (size < seeded.Count) size *= 2;
        var order = BracketOrder(size);
        var pairs = new List<Pairing>();
        for (var i = 0; i < order.Count; i += 2)
        {
            var a = order[i] <= seeded.Count ? seeded[order[i] - 1] : null;
            var b = order[i + 1] <= seeded.Count ? seeded[order[i + 1] - 1] : null;
            pairs.Add(a is null ? new Pairing(b!, null) : new Pairing(a, b));
        }
        return pairs;
    }

    /// <summary>Next knockout round: winners of adjacent matches meet.</summary>
    public static IReadOnlyList<Pairing> KnockoutNextRound(IReadOnlyList<string> winnersInBracketOrder)
    {
        var pairs = new List<Pairing>();
        for (var i = 0; i + 1 < winnersInBracketOrder.Count; i += 2)
            pairs.Add(new Pairing(winnersInBracketOrder[i], winnersInBracketOrder[i + 1]));
        return pairs;
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
        var seed = seeded.Select((name, i) => (name, i)).ToDictionary(t => t.name, t => t.i);
        var ranked = seeded.OrderByDescending(p => scores.GetValueOrDefault(p)).ThenBy(p => seed[p]).ToList();

        string? bye = null;
        if (ranked.Count % 2 == 1)
        {
            bye = ranked.LastOrDefault(p => !hadBye.Contains(p)) ?? ranked[^1];
            ranked.Remove(bye);
        }

        var budget = SearchBudget;
        var pairs = TryPair(ranked, opponents, allowRematch: false, ref budget);
        if (pairs is null)
        {
            budget = int.MaxValue; // with rematches allowed the first attempt always succeeds
            pairs = TryPair(ranked, opponents, allowRematch: true, ref budget)!;
        }
        var ordered = pairs.Select(p =>
        {
            var (a, b) = p;
            var wa = whiteCounts.GetValueOrDefault(a);
            var wb = whiteCounts.GetValueOrDefault(b);
            return wb < wa ? new Pairing(b, a) : new Pairing(a, b);
        }).ToList();
        return (ordered, bye);
    }

    /// <summary>Upper bound on backtracking steps before rematches are allowed.</summary>
    private const int SearchBudget = 200_000;

    private static List<(string, string)>? TryPair(List<string> ranked,
        IReadOnlyDictionary<string, HashSet<string>> opponents, bool allowRematch, ref int budget)
    {
        if (ranked.Count == 0) return [];
        if (--budget < 0) return null;
        var first = ranked[0];
        for (var j = 1; j < ranked.Count; j++)
        {
            var candidate = ranked[j];
            if (!allowRematch && opponents.TryGetValue(first, out var met) && met.Contains(candidate)) continue;
            var rest = ranked.Where((_, k) => k != 0 && k != j).ToList();
            var tail = TryPair(rest, opponents, allowRematch, ref budget);
            if (budget < 0) return null;
            if (tail is null) continue;
            tail.Insert(0, (first, candidate));
            return tail;
        }
        return null;
    }
}
