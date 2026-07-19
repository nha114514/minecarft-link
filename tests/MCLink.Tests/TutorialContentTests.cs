using MCLink.P2p.Tester;

namespace MCLink.Tests;

public sealed class TutorialContentTests
{
    [Fact]
    public void TutorialExplainsBothRolesAndTheDirectConnectionFallback()
    {
        Assert.Contains("对局域网开放", TutorialContent.HostSteps);
        Assert.Contains("邀请码", TutorialContent.HostSteps);
        Assert.Contains("回应码", TutorialContent.GuestSteps);
        Assert.Contains("127.0.0.1:", TutorialContent.GuestSteps);
        Assert.Contains("90 秒", TutorialContent.TroubleshootingSteps);
    }
}
