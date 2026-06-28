using Lantern.Contracts;

namespace Lantern.Engine;

/// <summary>
/// Mechanical content the engine reads. Holds ids/names/numbers/flags only — never KDM card
/// art or effect prose (see SPEC §6). Shippable data (gear, monster stat lines, deck-build
/// counts, formula config) is committed under content/packs/; the hit-location card list is
/// host-provided (content/local/, gitignored) and merged in by deck ref.
/// </summary>
public interface IContentPack
{
    string PackId { get; }
    FormulaConfig Formula { get; }
    bool TryGetMonster(string id, out MonsterDef monster);
    bool TryGetLevel(string monsterId, string levelId, out LevelDef level);
    bool TryGetGear(string id, out GearDef gear);
    IReadOnlyList<HitLocCardDef>? GetHitLocationDeck(string deckRef);
}

public sealed record FormulaConfig(int HitMin = 2, int HitMax = 10, int WoundMin = 2, int WoundMax = 10);

public sealed record GearDef(string Id, string Name, int Speed, int Accuracy, int Strength);

public sealed record LevelDef(string Id, MonsterStats Stats, int ToughnessWoundThreshold, string HitLocationDeckRef);

public sealed record MonsterDef(string Id, string Name, IReadOnlyDictionary<string, LevelDef> Levels);

/// <summary>A single hit-location card: mechanical flags + build count only (no prose).</summary>
public sealed record HitLocCardDef(string Id, string Name, bool HasCriticalSlot, bool IsTrap, int Count);

/// <summary>An in-memory content pack assembled by the loader (or tests).</summary>
public sealed class ContentPack(
    string packId,
    FormulaConfig formula,
    IReadOnlyDictionary<string, MonsterDef> monsters,
    IReadOnlyDictionary<string, GearDef> gear,
    IReadOnlyDictionary<string, IReadOnlyList<HitLocCardDef>> hitLocationDecks) : IContentPack
{
    public string PackId { get; } = packId;
    public FormulaConfig Formula { get; } = formula;

    public bool TryGetMonster(string id, out MonsterDef monster) => monsters.TryGetValue(id, out monster!);

    public bool TryGetLevel(string monsterId, string levelId, out LevelDef level)
    {
        level = null!;
        return monsters.TryGetValue(monsterId, out var m) && m.Levels.TryGetValue(levelId, out level!);
    }

    public bool TryGetGear(string id, out GearDef gear2) => gear.TryGetValue(id, out gear2!);

    public IReadOnlyList<HitLocCardDef>? GetHitLocationDeck(string deckRef) =>
        hitLocationDecks.TryGetValue(deckRef, out var d) ? d : null;
}
