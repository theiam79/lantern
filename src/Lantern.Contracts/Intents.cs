using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lantern.Contracts;

/// <summary>
/// One client→server gameplay message. The intent arrives as a raw <see cref="JsonElement"/>;
/// the server deserializes it into the closed <see cref="Intent"/> set manually (so an unknown
/// "type" is a clean rejection, not a SignalR binding exception).
/// </summary>
public sealed record IntentEnvelope(
    string RoomCode,
    string PlayerId,
    string PlayerToken,
    string ClientIntentId,
    JsonElement Intent
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StartShowdownIntent), "StartShowdown")]
[JsonDerivedType(typeof(AddSurvivorIntent), "AddSurvivor")]
[JsonDerivedType(typeof(DeclareAttackIntent), "DeclareAttack")]
[JsonDerivedType(typeof(EnterHitsIntent), "EnterHits")]
[JsonDerivedType(typeof(DrawHitLocationsIntent), "DrawHitLocations")]
[JsonDerivedType(typeof(EnterWoundIntent), "EnterWound")]
[JsonDerivedType(typeof(ApplyAttackResultIntent), "ApplyAttackResult")]
[JsonDerivedType(typeof(RecordMonsterTurnIntent), "RecordMonsterTurn")]
[JsonDerivedType(typeof(SetRoomPasswordIntent), "SetRoomPassword")]
[JsonDerivedType(typeof(UndoIntent), "Undo")]
[JsonDerivedType(typeof(HostOverrideIntent), "HostOverride")]
[JsonDerivedType(typeof(EndShowdownIntent), "EndShowdown")]
public abstract record Intent;

// ---- setup ----
public sealed record StartShowdownIntent(string MonsterId, string MonsterLevelId, ulong? MasterSeed) : Intent;

public sealed record AttributesDto(int Mov, int Acc, int Str, int Eva, int Lck, int Spd);

public sealed record AddSurvivorIntent(string SurvivorId, string Name, AttributesDto Attributes, string WeaponId)
    : Intent;

// ---- attack mini-FSM ----
public sealed record DeclareAttackIntent(string SurvivorId, string WeaponId, string TargetMonsterId) : Intent;

/// <summary>Mode "rolls": hits = count(r &gt;= hitsOn). Mode "count": trust <see cref="HitCount"/>.</summary>
public sealed record EnterHitsIntent(string AttackId, string Mode, int[]? Rolls, int? HitCount) : Intent;

public sealed record DrawHitLocationsIntent(string AttackId) : Intent;

/// <summary>Mode "roll": compare <see cref="Roll"/> vs woundsOn (+lantern crit). Mode "outcome": trust <see cref="Outcome"/>.</summary>
public sealed record EnterWoundIntent(string AttackId, string LocationCardId, string Mode, int? Roll, string? Outcome)
    : Intent;

public sealed record ApplyAttackResultIntent(string AttackId) : Intent;

// ---- manual monster turn (no AI deck in the MVP) ----
public sealed record ManualSurvivorWoundDto(string SurvivorId, int Amount);

public sealed record RecordMonsterTurnIntent(string Note, ManualSurvivorWoundDto? SurvivorWound) : Intent;

// ---- host-only ----
public sealed record SetRoomPasswordIntent(string? Password) : Intent;

public sealed record UndoIntent(int Count, long? TargetSeq) : Intent;

public sealed record HostOverrideIntent(StatePatchDto Patch, string Reason) : Intent;

public sealed record EndShowdownIntent(string Result) : Intent;

/// <summary>Typed, whitelisted patch (NOT a free JSON pointer). See SPEC §5.7.</summary>
public sealed record StatePatchDto(string Path, JsonElement Value);
