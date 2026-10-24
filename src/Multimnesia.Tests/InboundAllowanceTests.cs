using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class InboundAllowanceTests
{
    private static readonly DateTime Start = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_full_burst_is_accepted_and_the_next_message_is_refused()
    {
        var allowance = new InboundAllowance(burst: 5, perSecond: 2, Start);

        for (var i = 0; i < 5; i++) Assert.True(allowance.TryTake(Start));
        Assert.False(allowance.TryTake(Start));
    }

    [Fact]
    public void The_allowance_refills_at_its_rate()
    {
        var allowance = new InboundAllowance(burst: 5, perSecond: 2, Start);
        for (var i = 0; i < 5; i++) allowance.TryTake(Start);

        var later = Start.AddSeconds(1.5);
        Assert.True(allowance.TryTake(later));
        Assert.True(allowance.TryTake(later));
        Assert.True(allowance.TryTake(later));
        Assert.False(allowance.TryTake(later));
    }

    [Fact]
    public void The_allowance_never_refills_above_its_burst()
    {
        var allowance = new InboundAllowance(burst: 5, perSecond: 2, Start);
        allowance.TryTake(Start);

        var muchLater = Start.AddMinutes(10);
        for (var i = 0; i < 5; i++) Assert.True(allowance.TryTake(muchLater));
        Assert.False(allowance.TryTake(muchLater));
    }
}
