# KDM Mechanics Notes (rules discovery)

Research notes on Kingdom Death: Monster **mechanics & structure** (editions 1.5 / 1.6) to
guide Lantern's engine. **Mechanics only — no proprietary card text, stat values, or art.**
Gathered from public wikis/playthroughs (cited inline); treat exact numeric thresholds
(deck compositions, Survival Limit, hit-location die mapping) as needing a rulebook cross-check
before hardcoding.

---

## Engine impact — actionable corrections / TODO

Prioritized changes this implies for our current code (`Lantern.Engine`):

1. **`TargetNumbers` floor is wrong:** clamp floor should be **1, not 2** — base rules have no
   auto-miss/auto-fail on a natural 1. Upper clamp 10 is fine. (One-line fix in `TargetNumbers.cs`.)
2. **Wound roll needs the lantern-10 + critical branch:** a natural **10 always wounds** (even if
   `WoundsOn > 10`); it's a **critical wound** only if the drawn hit-location card is crit-capable —
   crit always wounds, fires the card's crit effect, and **cancels the monster's pending reactions**.
   Monster **Luck** shrinks the crit range. Our `ComputeWound` only handles `roll == 10 && hasCrit`;
   add the auto-wound-on-10 case and luck.
3. **Hits are per-die, one wound roll per hit:** roll `weapon.Speed` d10; each die ≥ `HitsOn` is a
   hit; **each hit draws its own hit-location card and gets its own wound roll.** (We currently model
   a single hit count then draw N — acceptable as a simplification, but the wound roll is per location.)
4. **Monsters have NO HP — model the AI-deck "wound stack" (victory condition):** each wound moves
   the **top AI card** to a wound pile; the monster **dies when a wound must be dealt but the AI deck
   is empty.** Our `MonsterState.TotalWounds >= ToughnessWoundThreshold` (HP model) is **mechanically
   wrong** — replace with the wound-stack model once we add the AI deck. (Bigger change; needs the AI deck.)
5. **Monster stat block:** `Speed / Accuracy / Damage / Evasion` are **runtime modifiers** (mostly 0
   at base); the real attack numbers live on **AI attack-profile cards**. `Evasion` should default 0
   and be token-driven, not a meaningful per-monster base. `Toughness` per level is correct but must be
   runtime-modifiable.
6. **Gear model is too thin:** add `type` (weapon/armor/item), `keywords[]`, `affinities`
   (red/green/blue per edge) + affinity-bonus flag, and for armor an `armor` value + `location`
   (head/arms/body/waist/legs). Weapons: Speed/Accuracy/Strength (+ Range/Reach for ranged).
7. **Survivor model (for the monster-offense half):** 5 hit locations (Head/Arms/Body/Waist/Legs)
   each with armor + light/heavy injury boxes; Head uses **Insanity** as armor → Brain Trauma;
   knockdown; **bleeding tokens (death at 5)**; severe-injury tables per location.

These are intentionally **not yet applied** — captured here so the next engine pass (and the
monster-offense build) can do them deliberately.

---

## Full report

### 1. Attacking the monster — to-HIT
Roll `weapon.Speed` d10; each die ≥ the weapon's **Accuracy** ("hits on X+") is a hit, counted
independently. Effective `HitsOn = weaponAccuracy − survivorAccuracy(+tokens/gear) + monsterEvasion
+ situational` (e.g. blind-spot −1). No auto-miss on natural 1 in base rules. **Evasion** is an
attribute but ~0 at base for most quarries; mostly situational. Sources: Attack/Attribute (Fandom),
mgpotter playthrough, BGG.

### 2. To-WOUND
Per hit, draw a hit-location card, roll `1d10 + survivorStrength + weaponStrength` vs monster
**Toughness**. `WoundsOn = Toughness − survivorStrength − weaponStrength` (success if d10 ≥ WoundsOn).
No auto-fail on 1. **Natural lantern 10 always wounds.** Lantern 10 is a **critical** only if the HL
card is crit-capable: crit always wounds, triggers crit effect (often a persistent injury), and
**cancels all the monster's reactions**. Monster **Luck** tokens shrink the crit range. Sources:
Showdown Phase, Critical Wound (Fandom), BGG.

### 3. Monster stat block
Movement; **Toughness** (wound threshold, per level, runtime-modifiable); **Speed/Accuracy/Damage**
= modifiers added to an AI **attack profile** (not standalone); **Evasion** (raises survivors' to-hit;
~0 base); **Luck** (shrinks crit range). Real attack numbers live on AI cards. Per-level data needed.

### 4. Showdown sequence
Setup (build AI + HL decks for the level, place survivors/monster, starting tokens). Rounds alternate
**Monster turn then Survivor turn**. Each survivor gets **1 movement + 1 activation** per round (any
order); activation is usually an attack. Monster turn = draw + resolve top AI card. Ends when the
monster dies (AI deck exhausted + a wound) or all survivors die.

### 5. Monster AI deck
Tiers: Basic / Advanced / Legendary (+ Special). Card types: Normal (one-shot), Mood/Trait
(persistent), Duration, Repeat. Built per level by tier counts, shuffled. Turn = draw top card, act.
Empty deck → shuffle discard back. Both empty → **Basic Action**. **Monster has no HP**: each wound
removes the top AI card to a wound stack; monster dies when a wound can't be paid.

### 6. Hit-location deck (survivor attacks)
Per hit, draw the top HL card; resolve the wound roll. **Reactions** resolve after the wound, before
discard (canceled by a critical). **Traps** cancel the attack's hits, hit the survivor, **end the
attack**, and **reshuffle the HL deck**. Crit-capable cards trigger on lantern 10; some inflict
persistent injuries. Reshuffle on Trap or exhaustion; otherwise discard after resolving.

### 7. Monster offense (to build)
AI **attack profile** = Speed (dice), Accuracy (to-hit), Damage, # hit locations, effects. Sequence:
resolve AI card + targeting (priority-target token) → attack roll (Speed d10; hit if ≥ profileAccuracy
+ monsterAccuracy + survivorEvasion) → roll a **hit-location die per hit** (Head/Arms/Body/Waist/Legs)
→ Before-Damage triggers → **Armor** (each point negates 1 damage and is spent) → wound: **Light →
Heavy → Severe** injury boxes (Head goes straight to Severe; uses **Insanity** as armor → Brain Trauma)
→ Severe Injury table per location (bleeding, maims, knockdown, death) → knockdown/death. Death at **5
bleeding tokens** or a fatal table result.

### 8. Survival actions
Core 5: **Dodge** (cancel a hit before damage), **Encourage** (stand an ally), **Surge** (+1
activation; Inner Lantern), **Dash** (+1 movement; Paint), **Endure**. Once per round each; cannot be
used while attacking; Doomed survivors can't. **Survival Limit** caps held survival (base value needs
a rulebook check). Sunstalker adds Overcharge/Embolden; 1.6 tweaks the gating set.

### 9. Knockdown / standing / dying
Knocked-down survivors lie down (no actions, no survival actions / can't dodge; likely auto-hit). Stand
automatically at end of next monster turn, or via Encourage. Mid-attack knockdown cancels the rest of
the attack. Bleeding tokens → **death at 5**; explicit "dead" results on injury/brain tables also kill.

### 10. Gear card anatomy (content schema)
Name; type (Weapon/Armor/Item, set membership); **keywords** (Melee/Ranged/Heavy/Two-handed/Slow/
Sharp/Set/…); weapon **attack profile** (Speed/Accuracy/Strength; +Range/Reach for ranged); armor
**value + single location**; special rules; **affinities** (red/green/blue half-squares on edges; two
matching adjacent halves = 1 affinity) + **affinity bonus**; **3×3 gear-grid** placement (adjacency
matters). Recommended extras: source/deck, archive qty, recipe, set bonuses.

---

## Edition / uncertainty flags
- 1.6 / Gambler's Chest: survival-action gating + terminology shift; core hit/wound/armor/wound-stack
  math unchanged. Verify the 1.6 default survival-action set.
- The wikis (kingdomdeath.wiki / fandom) block direct fetch (403); exact deck compositions, base
  Survival Limit, and the hit-location die→location mapping need a **physical rulebook cross-check**
  before hardcoding.
- Unverified: auto-hit on natural 10 for the *hit* roll (likely none); knocked-down survivors
  auto-hit (likely yes); exact killing-wound deck/discard interaction.
