using Lantern.Contracts;
using Lantern.Engine;

namespace Lantern.Engine.Tests;

internal static class TestPack
{
    public static readonly GearDef Sword = new("founding-sword", "Founding Sword", Speed: 3, Accuracy: 6, Strength: 3);

    public static readonly IReadOnlyList<HitLocCardDef> NoTrapDeck =
    [
        new("hl-crit", "A", HasCriticalSlot: true, IsTrap: false, Count: 4),
        new("hl-plain", "B", HasCriticalSlot: false, IsTrap: false, Count: 3),
    ];

    public static readonly IReadOnlyList<HitLocCardDef> TrapOnlyDeck =
    [
        new("hl-trap", "Trap", HasCriticalSlot: false, IsTrap: true, Count: 3),
    ];

    public static IContentPack Build(IReadOnlyList<HitLocCardDef> deck, int woundThreshold = 3)
    {
        var stats = new MonsterStats(Movement: 5, Toughness: 8, Speed: 1, Accuracy: 0, Damage: 1, Luck: 0, Evasion: 0);
        var level = new LevelDef("level-1", stats, woundThreshold, "host:wl-hitloc");
        var monster = new MonsterDef(
            "white-lion",
            "White Lion",
            new Dictionary<string, LevelDef> { ["level-1"] = level }
        );

        return new ContentPack(
            "test-pack",
            new FormulaConfig(),
            new Dictionary<string, MonsterDef> { ["white-lion"] = monster },
            new Dictionary<string, GearDef> { [Sword.Id] = Sword },
            new Dictionary<string, IReadOnlyList<HitLocCardDef>> { ["host:wl-hitloc"] = deck }
        );
    }
}
