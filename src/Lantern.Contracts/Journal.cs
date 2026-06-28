using System.Collections.Immutable;

namespace Lantern.Contracts;

/// <summary>
/// The committed unit of truth. One accepted gameplay intent → one entry at a monotonic
/// per-room <see cref="Seq"/> (seq 0 = Genesis, <see cref="Intent"/> null). State is a fold
/// (Reduce-replay) over the journal; nothing is ever deleted.
/// <para>
/// Undo is itself an entry whose <see cref="RevertedSeqs"/> lists the seqs it cancels; the
/// fold skips those entries. Host override is a normal <c>HostOverrideIntent</c> applied at
/// its own seq position.
/// </para>
/// </summary>
public sealed record JournalEntry(
    string RoomCode,
    long Seq,
    string ActorPlayerId,
    DateTimeOffset CommittedAt,
    Intent? Intent,
    ImmutableArray<long> RevertedSeqs,
    string? ClientIntentId
);
