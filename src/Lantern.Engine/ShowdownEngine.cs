using System.Collections.Immutable;
using Lantern.Contracts;

namespace Lantern.Engine;

public sealed record ReduceResult(bool Accepted, string? RejectReason, ShowdownState? State)
{
    public static ReduceResult Reject(string reason) => new(false, reason, null);
    public static ReduceResult Ok(ShowdownState state) => new(true, null, state);
}

/// <summary>
/// Pure, deterministic KDM showdown reducer (survivor-only MVP). Enforces only what it can see
/// (step ordering, status, seat/deck existence, computed target numbers); records everything
/// that depends on physical dice or card text. State is reconstructed by replaying intents
/// (<see cref="Fold"/>). IDs/seeds are minted by the caller and passed in (never generated here).
/// </summary>
public static class ShowdownEngine
{
    public static ReduceResult Reduce(ShowdownState? state, Intent intent, long seq, IContentPack pack)
    {
        if (intent is StartShowdownIntent start)
            return StartShowdown(state, start, seq, pack);

        if (state is null)
            return ReduceResult.Reject("no-showdown");

        return intent switch
        {
            AddSurvivorIntent a => AddSurvivor(state, a, seq),
            DeclareAttackIntent d => DeclareAttack(state, d, seq, pack),
            EnterHitsIntent e => EnterHits(state, e, seq),
            DrawHitLocationsIntent dl => DrawHitLocations(state, dl, seq, pack),
            EnterWoundIntent w => EnterWound(state, w, seq),
            ApplyAttackResultIntent ap => ApplyAttackResult(state, ap, seq),
            RecordMonsterTurnIntent r => state.Status == ShowdownStatus.Ended
                ? ReduceResult.Reject("showdown-ended")
                : ReduceResult.Ok(state with { MonsterTurnNote = r.Note, LastSeq = seq }),
            HostOverrideIntent ho => HostOverride(state, ho, seq),
            EndShowdownIntent => state.Status == ShowdownStatus.Ended
                ? ReduceResult.Reject("already-ended")
                : ReduceResult.Ok(state with { Status = ShowdownStatus.Ended, LastSeq = seq }),
            _ => ReduceResult.Reject("unhandled-intent"),
        };
    }

    /// <summary>Reconstruct state by replaying the journal tail onto a snapshot (skipping reverted entries).</summary>
    public static ShowdownState? Fold(ShowdownState? snapshot, IReadOnlyList<JournalEntry> tail, IContentPack pack)
    {
        var reverted = new HashSet<long>();
        foreach (var e in tail)
            foreach (var s in e.RevertedSeqs)
                reverted.Add(s);

        var state = snapshot;
        foreach (var e in tail.OrderBy(x => x.Seq))
        {
            if (e.Seq == 0 || e.Intent is null or UndoIntent) continue; // genesis / no-op revert markers
            if (reverted.Contains(e.Seq)) continue;
            var r = Reduce(state, e.Intent, e.Seq, pack);
            if (r.Accepted) state = r.State; // a replayed reject is a bug; keep prior state
        }
        return state;
    }

    // ---- intent handlers ----

    private static ReduceResult StartShowdown(ShowdownState? state, StartShowdownIntent si, long seq, IContentPack pack)
    {
        if (state is not null) return ReduceResult.Reject("already-started");
        if (si.MasterSeed is null) return ReduceResult.Reject("seed-not-resolved");
        if (!pack.TryGetLevel(si.MonsterId, si.MonsterLevelId, out var level)) return ReduceResult.Reject("unknown-monster-level");

        var cards = pack.GetHitLocationDeck(level.HitLocationDeckRef);
        if (cards is null || cards.Count == 0) return ReduceResult.Reject("missing-hit-location-deck");

        var (deck, cursors) = HitLocationDeck.Build(cards, RngCursors.Seeded(si.MasterSeed.Value));

        var monster = new MonsterState
        {
            Id = si.MonsterId,
            Base = level.Stats,
            ToughnessWoundThreshold = level.ToughnessWoundThreshold,
        };

        return ReduceResult.Ok(new ShowdownState
        {
            ShowdownId = $"sd-{seq}",
            ContractVersion = Protocol.Version,
            ContentPackId = pack.PackId,
            Status = ShowdownStatus.Idle,
            MonsterId = si.MonsterId,
            MonsterLevelId = si.MonsterLevelId,
            Monster = monster,
            Survivors = ImmutableDictionary<string, SurvivorCombatState>.Empty,
            Deck = deck,
            Rng = cursors,
            LastSeq = seq,
        });
    }

    private static ReduceResult AddSurvivor(ShowdownState state, AddSurvivorIntent a, long seq)
    {
        if (state.Status == ShowdownStatus.Ended) return ReduceResult.Reject("showdown-ended");
        var attrs = new SurvivorAttributes(a.Attributes.Mov, a.Attributes.Acc, a.Attributes.Str, a.Attributes.Eva, a.Attributes.Lck, a.Attributes.Spd);
        var sc = new SurvivorCombatState(a.SurvivorId, a.Name, attrs, a.WeaponId, false);
        return ReduceResult.Ok(state with { Survivors = state.Survivors.SetItem(a.SurvivorId, sc), LastSeq = seq });
    }

    private static ReduceResult DeclareAttack(ShowdownState state, DeclareAttackIntent d, long seq, IContentPack pack)
    {
        if (state.Status != ShowdownStatus.Idle) return ReduceResult.Reject("not-idle");
        if (!state.Survivors.TryGetValue(d.SurvivorId, out var surv)) return ReduceResult.Reject("unknown-survivor");
        if (!pack.TryGetGear(d.WeaponId, out var weapon)) return ReduceResult.Reject("unknown-weapon");

        var hitsOn = TargetNumbers.HitsOn(weapon, surv.Attributes, state.Monster.Base, pack.Formula);
        var attack = new AttackSequence(
            AttackId: $"atk-{seq}",
            SurvivorId: d.SurvivorId,
            WeaponId: d.WeaponId,
            TargetMonsterId: d.TargetMonsterId,
            AttackDice: weapon.Speed,
            HitsOn: hitsOn,
            EnteredHitCount: null,
            DrawnLocations: []);

        return ReduceResult.Ok(state with { Attack = attack, Status = ShowdownStatus.AwaitingHits, LastSeq = seq });
    }

    private static ReduceResult EnterHits(ShowdownState state, EnterHitsIntent e, long seq)
    {
        if (state.Status != ShowdownStatus.AwaitingHits || state.Attack?.AttackId != e.AttackId) return ReduceResult.Reject("bad-step");
        var count = e.Mode == "rolls"
            ? (e.Rolls?.Count(r => r >= state.Attack.HitsOn) ?? 0)
            : Math.Max(0, e.HitCount ?? 0);
        var status = count <= 0 ? ShowdownStatus.Resolved : ShowdownStatus.DrawingLocations;
        return ReduceResult.Ok(state with { Attack = state.Attack with { EnteredHitCount = count }, Status = status, LastSeq = seq });
    }

    private static ReduceResult DrawHitLocations(ShowdownState state, DrawHitLocationsIntent dl, long seq, IContentPack pack)
    {
        if (state.Status != ShowdownStatus.DrawingLocations || state.Attack?.AttackId != dl.AttackId) return ReduceResult.Reject("bad-step");
        if (!pack.TryGetLevel(state.MonsterId, state.MonsterLevelId, out var level)) return ReduceResult.Reject("unknown-monster-level");
        var cards = pack.GetHitLocationDeck(level.HitLocationDeckRef);
        if (cards is null) return ReduceResult.Reject("missing-hit-location-deck");
        if (!pack.TryGetGear(state.Attack.WeaponId, out var weapon)) return ReduceResult.Reject("unknown-weapon");
        if (!state.Survivors.TryGetValue(state.Attack.SurvivorId, out var surv)) return ReduceResult.Reject("unknown-survivor");

        var byId = cards.ToDictionary(c => c.Id);
        var count = state.Attack.EnteredHitCount ?? 0;
        var (drawnIds, deck, cursors) = HitLocationDeck.Draw(
            state.Deck, count, state.Rng, id => byId.TryGetValue(id, out var d) && d.IsTrap);
        var woundsOn = TargetNumbers.WoundsOn(weapon, surv.Attributes, state.Monster.Base, pack.Formula);

        var drawn = drawnIds.Select(id =>
        {
            var def = byId.GetValueOrDefault(id);
            return new DrawnLocation(id, def?.HasCriticalSlot ?? false, def?.IsTrap ?? false, woundsOn, null);
        }).ToImmutableArray();

        var trap = drawn.Any(l => l.IsTrap);
        var status = (trap || drawn.Length == 0) ? ShowdownStatus.Resolved : ShowdownStatus.AwaitingWounds;

        return ReduceResult.Ok(state with
        {
            Deck = deck,
            Rng = cursors,
            Attack = state.Attack with { DrawnLocations = drawn },
            Status = status,
            LastSeq = seq,
        });
    }

    private static ReduceResult EnterWound(ShowdownState state, EnterWoundIntent w, long seq)
    {
        if (state.Status != ShowdownStatus.AwaitingWounds || state.Attack?.AttackId != w.AttackId) return ReduceResult.Reject("bad-step");
        var locs = state.Attack.DrawnLocations;
        var idx = -1;
        for (var i = 0; i < locs.Length; i++)
            if (locs[i].CardId == w.LocationCardId && locs[i].Result is null) { idx = i; break; }
        if (idx < 0) return ReduceResult.Reject("location-not-found-or-resolved");

        var loc = locs[idx];
        var outcome = w.Mode == "roll"
            ? ComputeWound(w.Roll, loc.WoundsOn, loc.HasCriticalSlot)
            : ParseOutcome(w.Outcome);

        var updated = locs.SetItem(idx, loc with { Result = outcome });
        var allResolved = updated.All(l => l.Result is not null);
        return ReduceResult.Ok(state with
        {
            Attack = state.Attack with { DrawnLocations = updated },
            Status = allResolved ? ShowdownStatus.Resolved : ShowdownStatus.AwaitingWounds,
            LastSeq = seq,
        });
    }

    private static ReduceResult ApplyAttackResult(ShowdownState state, ApplyAttackResultIntent ap, long seq)
    {
        if (state.Status != ShowdownStatus.Resolved || state.Attack?.AttackId != ap.AttackId) return ReduceResult.Reject("bad-step");

        var wounds = state.Monster.WoundsByLocation;
        var total = state.Monster.TotalWounds;
        foreach (var loc in state.Attack.DrawnLocations)
        {
            if (loc.Result is WoundOutcome.Wound or WoundOutcome.Crit)
            {
                var inc = loc.Result == WoundOutcome.Crit ? 2 : 1; // loose; refine with real crit rules later
                wounds = wounds.SetItem(loc.CardId, wounds.GetValueOrDefault(loc.CardId) + inc);
                total += inc;
            }
        }

        var monster = state.Monster with { WoundsByLocation = wounds, TotalWounds = total };
        var deck = HitLocationDeck.DiscardDrawn(state.Deck);
        var defeated = total >= monster.ToughnessWoundThreshold;
        return ReduceResult.Ok(state with
        {
            Monster = monster,
            Deck = deck,
            Attack = null,
            Status = defeated ? ShowdownStatus.Ended : ShowdownStatus.Idle,
            LastSeq = seq,
        });
    }

    private static ReduceResult HostOverride(ShowdownState state, HostOverrideIntent ho, long seq)
    {
        var patch = ho.Patch;
        var path = patch.Path;
        try
        {
            if (path == "status")
            {
                var status = Enum.Parse<ShowdownStatus>(patch.Value.GetString() ?? "", ignoreCase: true);
                return ReduceResult.Ok(state with { Status = status, LastSeq = seq });
            }
            if (path == "monster.totalWounds")
                return ReduceResult.Ok(state with { Monster = state.Monster with { TotalWounds = patch.Value.GetInt32() }, LastSeq = seq });
            if (path.StartsWith("monster.woundsByLocation.", StringComparison.Ordinal))
            {
                var loc = path["monster.woundsByLocation.".Length..];
                var w = state.Monster.WoundsByLocation.SetItem(loc, patch.Value.GetInt32());
                return ReduceResult.Ok(state with { Monster = state.Monster with { WoundsByLocation = w }, LastSeq = seq });
            }
            if (path.StartsWith("survivor.", StringComparison.Ordinal) && path.EndsWith(".dead", StringComparison.Ordinal))
            {
                var id = path["survivor.".Length..^".dead".Length];
                if (!state.Survivors.TryGetValue(id, out var s)) return ReduceResult.Reject("unknown-survivor");
                var s2 = s with { Dead = patch.Value.GetBoolean() };
                return ReduceResult.Ok(state with { Survivors = state.Survivors.SetItem(id, s2), LastSeq = seq });
            }
            if (path.StartsWith("attack.", StringComparison.Ordinal) && path.EndsWith(".outcome", StringComparison.Ordinal))
            {
                if (state.Attack is null) return ReduceResult.Reject("no-attack-in-flight");
                var cardId = path["attack.".Length..^".outcome".Length];
                var locs = state.Attack.DrawnLocations;
                var idx = -1;
                for (var i = 0; i < locs.Length; i++) if (locs[i].CardId == cardId) { idx = i; break; }
                if (idx < 0) return ReduceResult.Reject("location-not-found");
                var outcome = Enum.Parse<WoundOutcome>(patch.Value.GetString() ?? "", ignoreCase: true);
                var updated = locs.SetItem(idx, locs[idx] with { Result = outcome });
                return ReduceResult.Ok(state with { Attack = state.Attack with { DrawnLocations = updated }, LastSeq = seq });
            }
            return ReduceResult.Reject("override-path-not-whitelisted");
        }
        catch (Exception)
        {
            return ReduceResult.Reject("override-bad-value");
        }
    }

    private static WoundOutcome ComputeWound(int? roll, int woundsOn, bool hasCrit)
    {
        if (roll is null) return WoundOutcome.Fail;
        if (roll == 10 && hasCrit) return WoundOutcome.Crit;
        return roll >= woundsOn ? WoundOutcome.Wound : WoundOutcome.Fail;
    }

    private static WoundOutcome ParseOutcome(string? outcome) => outcome switch
    {
        "wound" => WoundOutcome.Wound,
        "crit" => WoundOutcome.Crit,
        _ => WoundOutcome.Fail,
    };
}
