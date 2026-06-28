using System.Collections.Immutable;
using Lantern.Contracts;

namespace Lantern.Engine;

/// <summary>The one deck the app runs. Seeded build/draw with reshuffle-and-continue.</summary>
public static class HitLocationDeck
{
    public const string Substream = "hitloc-shuffle";

    public static (HitLocationDeckState Deck, RngCursors Cursors) Build(IReadOnlyList<HitLocCardDef> cards, RngCursors cursors)
    {
        var multiset = new List<string>();
        foreach (var c in cards)
            for (var i = 0; i < c.Count; i++)
                multiset.Add(c.Id);

        var (perm, next) = RngEngine.Shuffle(cursors, Substream, multiset.Count);
        var drawPile = perm.Select(i => multiset[i]).ToImmutableArray();
        return (new HitLocationDeckState(drawPile, [], []), next);
    }

    /// <summary>
    /// Pop up to <paramref name="count"/> cards; reshuffle the discard back in if the draw pile
    /// empties. If <paramref name="stopAfter"/> is supplied and a popped card matches (a Trap),
    /// the draw stops immediately after including it (the rest of the count is left undrawn).
    /// </summary>
    public static (ImmutableArray<string> Drawn, HitLocationDeckState Deck, RngCursors Cursors) Draw(
        HitLocationDeckState deck, int count, RngCursors cursors, Func<string, bool>? stopAfter = null)
    {
        var drawPile = deck.DrawPile.ToList();
        var discard = deck.DiscardPile.ToList();
        var drawn = new List<string>();
        var cur = cursors;

        for (var k = 0; k < count; k++)
        {
            if (drawPile.Count == 0)
            {
                if (discard.Count == 0) break; // deck exhausted
                var (perm, next) = RngEngine.Shuffle(cur, Substream, discard.Count);
                drawPile = perm.Select(i => discard[i]).ToList();
                discard.Clear();
                cur = next;
            }

            var top = drawPile[0];
            drawPile.RemoveAt(0);
            drawn.Add(top);
            if (stopAfter?.Invoke(top) == true) break; // Trap ends the draw immediately
        }

        var newDeck = deck with
        {
            DrawPile = [.. drawPile],
            DiscardPile = [.. discard],
            DrawnThisAttack = [.. deck.DrawnThisAttack, .. drawn],
        };
        return ([.. drawn], newDeck, cur);
    }

    /// <summary>Move this attack's drawn cards into the discard pile.</summary>
    public static HitLocationDeckState DiscardDrawn(HitLocationDeckState deck) => deck with
    {
        DiscardPile = [.. deck.DiscardPile, .. deck.DrawnThisAttack],
        DrawnThisAttack = [],
    };
}
