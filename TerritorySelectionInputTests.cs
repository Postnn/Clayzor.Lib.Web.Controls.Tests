using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class TerritorySelectionInputTests
{
    [Theory]
    [InlineData(null, true, "")]
    [InlineData("", true, "")]
    [InlineData("107, 9,107", true, "9,107")]
    [InlineData("107,0", false, "")]
    [InlineData("107,", false, "")]
    [InlineData("-1", false, "")]
    [InlineData("2147483648", false, "")]
    public void ParsesOnlyCompletePositiveSelections(string? input, bool valid, string expected)
    {
        Assert.Equal(valid, TerritorialEntityRoutes.TrySelectedIds(input, out var ids));
        Assert.Equal(expected, string.Join(",", ids));
    }
}
