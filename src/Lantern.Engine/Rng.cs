using System.Collections.Immutable;
using Lantern.Contracts;

namespace Lantern.Engine;

/// <summary>
/// Deterministic, replayable RNG. App-owned randomness only (deck shuffles) — dice are physical.
/// A substream is seeded from the master seed + a stable hash of its label; the per-substream
/// cursor is the number of u64 draws consumed, so any state is reproducible from
/// <see cref="RngCursors"/> alone. Never uses System.Random / DateTime.
/// </summary>
public static class RngEngine
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Fnv1a(string s)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (var ch in s)
        {
            h ^= ch;
            h *= 0x100000001B3UL;
        }
        return h;
    }

    private static ulong SubstreamSeed(ulong masterSeed, string substream) => Mix(masterSeed ^ Fnv1a(substream));

    /// <summary>
    /// Fisher-Yates permutation of [0, n). Returns the permutation and the advanced cursors.
    /// Resumes from the substream's stored cursor so repeated folds are identical.
    /// </summary>
    public static (ImmutableArray<int> Permutation, RngCursors Cursors) Shuffle(
        RngCursors cursors,
        string substream,
        int n
    )
    {
        var seed = SubstreamSeed(cursors.MasterSeed, substream);
        var cur = cursors.PerSubstream.GetValueOrDefault(substream, 0UL);
        // state is positioned so the next Next() yields the (cur+1)-th output.
        var state = seed + cur * Gamma;
        ulong drawn = 0;

        var arr = new int[n];
        for (var i = 0; i < n; i++)
            arr[i] = i;
        for (var i = n - 1; i >= 1; i--)
        {
            state += Gamma;
            drawn++;
            var r = Mix(state);
            var j = (int)(r % (ulong)(i + 1));
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }

        var next = cursors with { PerSubstream = cursors.PerSubstream.SetItem(substream, cur + drawn) };
        return ([.. arr], next);
    }
}
