using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Layout;

namespace Clayzor.Lib.Web.Controls.Tests;

public class ClayFormHelpTests
{
    [Theory]
    [InlineData("https://docs.example/wiki")]
    [InlineData("https://docs.example/wiki/")]
    public void ResolvesDirectoryWithoutLosingBasePath(string configuredBase)
    {
        var form = new ClayFormHelp("/Inventory/Раздел 1/раздел%20два/% #?.aspx/");
        var result = ClayFormHelpUrl.Resolve(configuredBase, form);

        Assert.Equal(
            "https://docs.example/wiki/Inventory/%D0%A0%D0%B0%D0%B7%D0%B4%D0%B5%D0%BB%201/" +
            "%D1%80%D0%B0%D0%B7%D0%B4%D0%B5%D0%BB%20%D0%B4%D0%B2%D0%B0/%25%20%23%3F.aspx",
            result);
    }

    [Fact]
    public void LegacyPrefixEncodesThePathAsOneQueryValue()
    {
        var form = new ClayFormHelp("/Inventory/Путь%20%23?/");
        var result = ClayFormHelpUrl.Resolve("https://docs.example/wiki?language=ru&page=", form);

        Assert.Equal("https://docs.example/wiki?language=ru&page=Inventory%2F%D0%9F%D1%83%D1%82%D1%8C+%23%3F", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//docs.example/wiki")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://username:password@docs.example/wiki")]
    [InlineData("https://docs.example/wiki#section")]
    [InlineData("https://docs.example/wiki?language=ru")]
    [InlineData("https://docs.example/wiki?")]
    [InlineData("https://docs.example/wiki?language=ru&")]
    [InlineData("https://docs.example/wiki?=")]
    public void InvalidBasesDoNotProduceLinks(string? configuredBase)
    {
        Assert.Null(ClayFormHelpUrl.Resolve(configuredBase, new ClayFormHelp("Inventory/Page.aspx")));
    }

    [Theory]
    [InlineData("//evil.example/page")]
    [InlineData("https://evil.example/page")]
    [InlineData("../Page.aspx")]
    [InlineData("Inventory/%2e%2e/Page.aspx")]
    [InlineData("Inventory/%2fPage.aspx")]
    [InlineData("Inventory/%5cPage.aspx")]
    public void UnexpectedMetadataDoNotProduceLinks(string path)
    {
        Assert.Null(ClayFormHelpUrl.Resolve("https://docs.example/wiki/", new ClayFormHelp(path)));
    }

    [Theory]
    [InlineData("Equipment/PingHost")]
    [InlineData("Equipment/PingHost.aspx")]
    [InlineData("equipment/pinghost.aspx/?id=42&token=doNotCopy#draft")]
    public void PingHostAliasesUseRegisteredPathOnly(string relativeLocation)
    {
        var form = InventoryFormHelp.Find(relativeLocation);
        var result = ClayFormHelpUrl.Resolve("https://docs.example/wiki/", form);

        Assert.Equal("https://docs.example/wiki/Inventory/Equipment/PingHost.aspx", result);
    }

    [Fact]
    public void UnregisteredRouteDoesNotResolveFromQuery()
    {
        Assert.Null(InventoryFormHelp.Find("Equipment/Other?route=Equipment/PingHost"));
        Assert.Null(ClayFormHelpUrl.Resolve("https://docs.example/wiki/", null));
    }
}
