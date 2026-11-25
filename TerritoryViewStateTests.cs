using Clayzor.Lib.Entities.TerritorialEntity;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class TerritoryViewStateTests
{
    [Fact]
    public void ReadsSingleAnchorAndSelectionFromLegacyJstree()
    {
        var state = TerritoryViewState.Read("""{"core":{"open":["0","3",107],"selected":[0,"107","19"]}}""");
        Assert.Equal(new TerritoryViewState(107, 107, false), state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("[]")]
    public void RejectsInvalidEnvelope(string? json) => Assert.Null(TerritoryViewState.Read(json));

    [Fact]
    public void RoundTripsResetAndPhoneCodePane()
    {
        foreach (var state in new[] { new TerritoryViewState(null,null,false),new TerritoryViewState(12,19,true) })
            Assert.Equal(state, TerritoryViewState.Read(state.Serialize()));
    }
}
