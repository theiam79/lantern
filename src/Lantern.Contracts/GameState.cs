using System.Collections.Immutable;

namespace Lantern.Contracts;

// Shared game-state records. These are pure data — ids, counters, and numbers only
// (no KDM card text/art), so the same type is the engine aggregate AND the wire state.
// Behavior (Reduce/Fold/RNG/target numbers) lives in Lantern.Engine.

public enum ShowdownStatus
{
    Idle,
    AwaitingHits,
    DrawingLocations,
    AwaitingWounds,
    Resolved,
    Ended,
}

public enum WoundOutcome
{
    Wound,
    Fail,
    Crit,
}

public sealed record MonsterStats(
    int Movement,
    int Toughness,
    int Speed,
    int Accuracy,
    int Damage,
    int Luck,
    int Evasion
);

public sealed record MonsterState
{
    public required string Id { get; init; }
    public required MonsterStats Base { get; init; }
    public ImmutableDictionary<string, int> WoundsByLocation { get; init; } = ImmutableDictionary<string, int>.Empty;
    public int TotalWounds { get; init; }
    public required int ToughnessWoundThreshold { get; init; }
}

public sealed record SurvivorAttributes(int Mov, int Acc, int Str, int Eva, int Lck, int Spd);

public sealed record SurvivorCombatState(
    string SurvivorId,
    string Name,
    SurvivorAttributes Attributes,
    string WeaponId,
    bool Dead
);

public sealed record DrawnLocation(
    string CardId,
    bool HasCriticalSlot,
    bool IsTrap,
    int WoundsOn,
    WoundOutcome? Result
);

public sealed record AttackSequence(
    string AttackId,
    string SurvivorId,
    string WeaponId,
    string TargetMonsterId,
    int AttackDice,
    int HitsOn,
    int? EnteredHitCount,
    ImmutableArray<DrawnLocation> DrawnLocations
);

public sealed record HitLocationDeckState(
    ImmutableArray<string> DrawPile,
    ImmutableArray<string> DiscardPile,
    ImmutableArray<string> DrawnThisAttack
);

public sealed record RngCursors(ulong MasterSeed, ImmutableDictionary<string, ulong> PerSubstream)
{
    public static RngCursors Seeded(ulong masterSeed) => new(masterSeed, ImmutableDictionary<string, ulong>.Empty);
}

public sealed record ShowdownState
{
    public required string ShowdownId { get; init; }
    public required int ContractVersion { get; init; }
    public required string ContentPackId { get; init; }
    public required ShowdownStatus Status { get; init; }
    public required string MonsterId { get; init; }
    public required string MonsterLevelId { get; init; }
    public required MonsterState Monster { get; init; }
    public required ImmutableDictionary<string, SurvivorCombatState> Survivors { get; init; }
    public required HitLocationDeckState Deck { get; init; }
    public AttackSequence? Attack { get; init; }
    public string? MonsterTurnNote { get; init; }
    public required RngCursors Rng { get; init; }
    public long LastSeq { get; init; }
}
