namespace Lantern.Engine;

/// <summary>The top-level campaign loop phases.</summary>
public enum CampaignPhase
{
    Settlement,
    Hunt,
    Showdown,
}

/// <summary>
/// Pure, deterministic transitions for the top-level campaign loop.
/// Seed for the engine; the fuller phase/turn + showdown state machine builds out from here.
/// </summary>
public static class CampaignLoop
{
    /// <summary>The canonical loop: Settlement → Hunt → Showdown → Settlement → …</summary>
    public static CampaignPhase Next(CampaignPhase current) => current switch
    {
        CampaignPhase.Settlement => CampaignPhase.Hunt,
        CampaignPhase.Hunt => CampaignPhase.Showdown,
        CampaignPhase.Showdown => CampaignPhase.Settlement,
        _ => throw new ArgumentOutOfRangeException(nameof(current), current, "Unknown campaign phase."),
    };
}
