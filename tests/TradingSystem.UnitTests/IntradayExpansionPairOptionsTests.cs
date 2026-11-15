using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.UnitTests;

public sealed class IntradayExpansionPairOptionsTests
{
    [Theory]
    [InlineData("nifty")]
    [InlineData("sensex")]
    [InlineData("banknifty")]
    [InlineData("NIFTY")]
    public void AllNewEntriesAreHeldWithoutDisablingManagement(string market)
    {
        var options = new IntradayExpansionPairOptions();
        Assert.True(options.Enabled);
        Assert.False(options.AllowsNewEntry(market));
    }

    [Fact]
    public void NiftyOverrideCannotBypassGlobalHold()
    {
        var options = new IntradayExpansionPairOptions
        {
            NiftyEntryEnabled = true
        };

        Assert.False(options.AllowsNewEntry("nifty"));
        Assert.False(options.AllowsNewEntry("sensex"));
    }

    [Fact]
    public void GlobalReenableStillHonorsNiftySpecificHold()
    {
        var options = new IntradayExpansionPairOptions { NewEntriesEnabled = true };
        Assert.False(options.AllowsNewEntry("nifty"));
        Assert.True(options.AllowsNewEntry("sensex"));
        Assert.True(new IntradayExpansionPairOptions
            { NewEntriesEnabled = true, NiftyEntryEnabled = true }.AllowsNewEntry("nifty"));
    }
}
