using Lantern.Engine;

namespace Lantern.Engine.Tests;

public class CampaignLoopTests
{
    [Test]
    public async Task Loop_cycles_settlement_hunt_showdown_and_back()
    {
        var phase = CampaignPhase.Settlement;

        phase = CampaignLoop.Next(phase);
        await Assert.That(phase).IsEqualTo(CampaignPhase.Hunt);

        phase = CampaignLoop.Next(phase);
        await Assert.That(phase).IsEqualTo(CampaignPhase.Showdown);

        phase = CampaignLoop.Next(phase);
        await Assert.That(phase).IsEqualTo(CampaignPhase.Settlement);
    }
}
