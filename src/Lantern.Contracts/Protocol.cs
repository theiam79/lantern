namespace Lantern.Contracts;

/// <summary>
/// The versioned wire contract shared by every client (Svelte web today; three.js / Godot / Unity later).
/// Bump <see cref="Version"/> on any breaking change to intents or state deltas.
/// </summary>
public static class Protocol
{
    /// <summary>Bump on ANY breaking change to intents or state.</summary>
    public const int Version = 1;

    /// <summary>Server accepts requests in [MinSupported, Version].</summary>
    public const int MinSupported = 1;
}

/// <summary>
/// Envelope for every server→client message: a monotonic per-room <paramref name="Seq"/> plus a typed payload.
/// Clients apply a full snapshot on join, then strictly increasing deltas; a seq gap triggers a resync.
/// </summary>
public sealed record ServerMessage<T>(int ContractVersion, long Seq, string Type, T Payload);
