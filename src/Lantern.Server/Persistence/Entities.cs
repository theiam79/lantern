namespace Lantern.Server.Persistence;

/// <summary>Room registry + lobby status.</summary>
public sealed class RoomRow
{
    public required string RoomCode { get; set; }
    public required string ContentPackId { get; set; }
    public string? RoomPasswordHash { get; set; }
    public required string HostPlayerId { get; set; }
    public long LastSeq { get; set; }
    public required string Status { get; set; } // "lobby" | "showdown" | "ended"
    public int ContractVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Auth roster (one per player per room). Tokens are stored hashed.</summary>
public sealed class RoomMemberRow
{
    public required string RoomCode { get; set; }
    public required string PlayerId { get; set; }
    public required string DisplayName { get; set; }
    public bool IsHost { get; set; }
    public required string PlayerTokenHash { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>Append-only source of truth (INSERT only). Unique (RoomCode, Seq) and (RoomCode, ClientIntentId).</summary>
public sealed class JournalRow
{
    public required string RoomCode { get; set; }
    public long Seq { get; set; }
    public required string ActorPlayerId { get; set; }
    public string? IntentJson { get; set; } // serialized Lantern.Contracts.Intent (polymorphic); null = genesis
    public string? RevertedSeqsJson { get; set; } // JSON long[]; set on undo entries
    public string? ClientIntentId { get; set; } // idempotency key
    public DateTimeOffset CommittedAt { get; set; }
}

/// <summary>Fold cache (keep latest few per room). StateJson null = pre-showdown (lobby).</summary>
public sealed class SnapshotRow
{
    public required string RoomCode { get; set; }
    public long Seq { get; set; }
    public int ContractVersion { get; set; }
    public string? StateJson { get; set; } // serialized Lantern.Contracts.ShowdownState
    public DateTimeOffset At { get; set; }
}
