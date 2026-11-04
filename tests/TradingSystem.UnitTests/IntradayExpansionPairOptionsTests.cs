using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.UnitTests;

public sealed class IntradayExpansionPairOptionsTests
{
    [Fact]
    public void NiftyNewEntriesAreHeldWithoutBlockingSensex()
    {
        var options = new IntradayExpansionPairOptions();

        Assert.False(options.AllowsNewEntry("nifty"));
        Assert.True(options.AllowsNewEntry("sensex"));
    }

    [Fact]
    public void NiftyRequiresExplicitReenable()
    {
        var options = new IntradayExpansionPairOptions
        {
            NiftyEntryEnabled = true
        };

        Assert.True(options.AllowsNewEntry("nifty"));
    }
}
