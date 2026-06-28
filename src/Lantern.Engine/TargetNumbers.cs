using Lantern.Contracts;

namespace Lantern.Engine;

/// <summary>
/// "Hits on X+" / "wounds on Y+" computation. The app shows these; players roll physical dice.
/// LOOSE and isolated here so it is a one-line change. The exact direction/inputs are
/// rulebook-ambiguous — ASSUMPTION, verify against the physical rulebook (SPEC §10/§11.10).
/// </summary>
public static class TargetNumbers
{
    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    /// <summary>
    /// Default: the weapon's own Accuracy is the base hit number, made easier by the survivor's
    /// Accuracy attribute and harder by the monster's Evasion.
    /// </summary>
    public static int HitsOn(GearDef weapon, SurvivorAttributes survivor, MonsterStats monster, FormulaConfig f) =>
        Clamp(weapon.Accuracy - survivor.Acc + monster.Evasion, f.HitMin, f.HitMax);

    /// <summary>
    /// Default: the monster's Toughness, made easier by survivor Strength + weapon Strength.
    /// </summary>
    public static int WoundsOn(GearDef weapon, SurvivorAttributes survivor, MonsterStats monster, FormulaConfig f) =>
        Clamp(monster.Toughness - survivor.Str - weapon.Strength, f.WoundMin, f.WoundMax);
}
