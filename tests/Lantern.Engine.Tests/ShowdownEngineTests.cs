using System.Collections.Immutable;
using Lantern.Contracts;
using Lantern.Engine;

namespace Lantern.Engine.Tests;

public class ShowdownEngineTests
{
    private static readonly AttributesDto Attrs = new(Mov: 5, Acc: 0, Str: 0, Eva: 0, Lck: 0, Spd: 0);

    // Helper: apply an intent, assert accepted, record the journal entry, return new state.
    private static ShowdownState Step(ref List<JournalEntry> journal, ShowdownState? state, Intent intent, long seq, IContentPack pack)
    {
        var r = ShowdownEngine.Reduce(state, intent, seq, pack);
        if (!r.Accepted) throw new Exception($"intent {intent.GetType().Name} rejected: {r.RejectReason}");
        journal.Add(new JournalEntry("R", seq, "p1", DateTimeOffset.UnixEpoch, intent, [], $"c{seq}"));
        return r.State!;
    }

    [Test]
    public async Task Full_survivor_attack_applies_wounds()
    {
        var pack = TestPack.Build(TestPack.NoTrapDeck);
        var journal = new List<JournalEntry>();
        ShowdownState? s = null;

        s = Step(ref journal, s, new StartShowdownIntent("white-lion", "level-1", 42UL), 1, pack);
        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.Idle);

        s = Step(ref journal, s, new AddSurvivorIntent("s1", "Allister", Attrs, TestPack.Sword.Id), 2, pack);

        s = Step(ref journal, s, new DeclareAttackIntent("s1", TestPack.Sword.Id, "white-lion"), 3, pack);
        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.AwaitingHits);
        await Assert.That(s.Attack!.AttackId).IsEqualTo("atk-3");
        await Assert.That(s.Attack!.AttackDice).IsEqualTo(3);   // weapon speed
        await Assert.That(s.Attack!.HitsOn).IsEqualTo(6);       // weapon acc 6 - surv 0 + evasion 0

        s = Step(ref journal, s, new EnterHitsIntent("atk-3", "count", null, 2), 4, pack);
        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.DrawingLocations);

        s = Step(ref journal, s, new DrawHitLocationsIntent("atk-3"), 5, pack);
        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.AwaitingWounds);
        await Assert.That(s.Attack!.DrawnLocations.Length).IsEqualTo(2);
        await Assert.That(s.Attack!.DrawnLocations[0].WoundsOn).IsEqualTo(5); // tough 8 - str 0 - weapon 3

        long seq = 6;
        foreach (var loc in s.Attack!.DrawnLocations)
            s = Step(ref journal, s, new EnterWoundIntent("atk-3", loc.CardId, "outcome", null, "wound"), seq++, pack);
        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.Resolved);

        s = Step(ref journal, s, new ApplyAttackResultIntent("atk-3"), seq++, pack);
        await Assert.That(s.Monster.TotalWounds).IsEqualTo(2);
        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.Idle);
        await Assert.That(s.Attack).IsNull();

        // Fold from genesis must reproduce the incrementally-reduced state.
        var folded = ShowdownEngine.Fold(null, journal, pack);
        await Assert.That(folded!.Monster.TotalWounds).IsEqualTo(s.Monster.TotalWounds);
        await Assert.That(folded!.Status).IsEqualTo(s.Status);
        await Assert.That(folded!.Deck.DrawPile.Length).IsEqualTo(s.Deck.DrawPile.Length);
    }

    [Test]
    public async Task Deck_build_is_deterministic_for_a_seed()
    {
        var (a, _) = HitLocationDeck.Build(TestPack.NoTrapDeck, RngCursors.Seeded(123UL));
        var (b, _) = HitLocationDeck.Build(TestPack.NoTrapDeck, RngCursors.Seeded(123UL));
        var (c, _) = HitLocationDeck.Build(TestPack.NoTrapDeck, RngCursors.Seeded(999UL));

        await Assert.That(a.DrawPile.SequenceEqual(b.DrawPile)).IsTrue();
        await Assert.That(a.DrawPile.Length).IsEqualTo(7);
        // Overwhelmingly likely different order for a different seed.
        await Assert.That(a.DrawPile.SequenceEqual(c.DrawPile)).IsFalse();
    }

    [Test]
    public async Task Trap_card_ends_the_attack_immediately()
    {
        var pack = TestPack.Build(TestPack.TrapOnlyDeck);
        var journal = new List<JournalEntry>();
        ShowdownState? s = null;
        s = Step(ref journal, s, new StartShowdownIntent("white-lion", "level-1", 7UL), 1, pack);
        s = Step(ref journal, s, new AddSurvivorIntent("s1", "Lucy", Attrs, TestPack.Sword.Id), 2, pack);
        s = Step(ref journal, s, new DeclareAttackIntent("s1", TestPack.Sword.Id, "white-lion"), 3, pack);
        s = Step(ref journal, s, new EnterHitsIntent("atk-3", "count", null, 1), 4, pack);
        s = Step(ref journal, s, new DrawHitLocationsIntent("atk-3"), 5, pack);

        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.Resolved); // trap -> straight to Resolved
        await Assert.That(s.Attack!.DrawnLocations.Any(l => l.IsTrap)).IsTrue();
    }

    [Test]
    public async Task Reaching_wound_threshold_ends_the_showdown()
    {
        var pack = TestPack.Build(TestPack.NoTrapDeck, woundThreshold: 1);
        var journal = new List<JournalEntry>();
        ShowdownState? s = null;
        s = Step(ref journal, s, new StartShowdownIntent("white-lion", "level-1", 1UL), 1, pack);
        s = Step(ref journal, s, new AddSurvivorIntent("s1", "Zach", Attrs, TestPack.Sword.Id), 2, pack);
        s = Step(ref journal, s, new DeclareAttackIntent("s1", TestPack.Sword.Id, "white-lion"), 3, pack);
        s = Step(ref journal, s, new EnterHitsIntent("atk-3", "count", null, 1), 4, pack);
        s = Step(ref journal, s, new DrawHitLocationsIntent("atk-3"), 5, pack);
        var loc = s.Attack!.DrawnLocations[0];
        s = Step(ref journal, s, new EnterWoundIntent("atk-3", loc.CardId, "outcome", null, "wound"), 6, pack);
        s = Step(ref journal, s, new ApplyAttackResultIntent("atk-3"), 7, pack);

        await Assert.That(s.Status).IsEqualTo(ShowdownStatus.Ended);
    }

    [Test]
    public async Task Undo_reverts_an_entry_in_the_fold()
    {
        var pack = TestPack.Build(TestPack.NoTrapDeck);
        var journal = new List<JournalEntry>();
        ShowdownState? s = null;
        s = Step(ref journal, s, new StartShowdownIntent("white-lion", "level-1", 5UL), 1, pack);
        s = Step(ref journal, s, new AddSurvivorIntent("s1", "Ezra", Attrs, TestPack.Sword.Id), 2, pack);
        s = Step(ref journal, s, new AddSurvivorIntent("s2", "Nico", Attrs, TestPack.Sword.Id), 3, pack);

        // Revert seq 3 (adding s2): an Undo entry naming the reverted seq.
        journal.Add(new JournalEntry("R", 4, "p1", DateTimeOffset.UnixEpoch, new UndoIntent(1, null), [3L], "c4"));

        var folded = ShowdownEngine.Fold(null, journal, pack);
        await Assert.That(folded!.Survivors.ContainsKey("s1")).IsTrue();
        await Assert.That(folded!.Survivors.ContainsKey("s2")).IsFalse();
    }
}
