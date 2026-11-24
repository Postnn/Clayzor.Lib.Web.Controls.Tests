using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.TerritorialEntity;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.WebUtilities;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class TerritorialEntityTests
{
    [Theory]
    [InlineData("1,2.3", 123)]
    [InlineData("1\u00a0234", 1234)]
    [InlineData("", null)]
    public void ClassificationRemovesLegacySeparatorsWithoutParsingADecimal(string text, int? expected)
    {
        Assert.True(TerritorialEntityRules.TryParseCountryClassification(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData(".")]
    public void InvalidClassificationRemainsAnError(string text) =>
        Assert.False(TerritorialEntityRules.TryParseCountryClassification(text, out _));

    [Fact]
    public void RootScopeUsesSuppliedPriorityAndNeverWidensInvalidExplicitInput()
    {
        Assert.True(TerritorialEntityRoutes.TryRoots("10,1,10", "8", "9", out var roots));
        Assert.Equal([1, 10], roots);
        Assert.True(TerritorialEntityRoutes.TryRoots(null, "8", "9", out roots));
        Assert.Equal([8], roots);
        Assert.False(TerritorialEntityRoutes.TryRoots("bad,10", "8", "9", out _));
        Assert.False(TerritorialEntityRoutes.TryRoots("0", null, null, out _));
        Assert.False(TerritorialEntityRoutes.TryRoots(null, "1,2", null, out _));
        Assert.True(TerritorialEntityRoutes.TryRoots(null, null, null, out roots));
        Assert.Null(roots);
    }

    [Fact]
    public void GridLinksUseConfirmedFixedMetadataAndForcedContext()
    {
        static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Args(string url) =>
            QueryHelpers.ParseQuery(url[url.IndexOf('?')..]);
        var territory = Args(TerritorialEntityRoutes.Grid(Запросы.Территории, 17));
        Assert.Equal("1030", territory["id"].ToString());
        Assert.Equal("eq~17", territory["Parent"].ToString());
        Assert.False(territory.ContainsKey("CLID"));
        var phone = Args(TerritorialEntityRoutes.Grid(Запросы.Телефонные_коды, 17, TerritorialEntityRoutes.PhoneListContext));
        Assert.Equal("9993", phone["id"].ToString());
        Assert.Equal("eq~17", phone["areaid"].ToString());
        Assert.Equal("85", phone["CLID"].ToString());
    }

    [Theory]
    [InlineData("http://localhost:5650", true)]
    [InlineData("*", false)]
    [InlineData("http://localhost:5651", false)]
    [InlineData("http://localhost:5650/other", false)]
    public void SelectionCallbackRequiresExactApplicationOrigin(string origin, bool expected)
    {
        Assert.Equal(expected, TerritorialEntityRoutes.IsLocalReturn(Guid.NewGuid().ToString("N"), origin, "http://localhost:5650/"));
    }

    [Fact]
    public async Task RootReadIsParameterizedAndDistinguishesSqlErrorFromAnEmptyLevel()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(), Script.Error(new InvalidOperationException("read failed"))]));
        using var db = new DbManager("Server=test", null, () => connection);
        Assert.Empty(await TerritorialEntityData.ReadTreeLevelAsync(db, 47, [10, 12]));
        Assert.Contains("r.L<=t.L AND t.R<=r.R", connection.CommandLog[0]);
        Assert.Equal(47, connection.ParameterLog[0]["ParentId"]);
        Assert.True((bool)connection.ParameterLog[0]["Restricted"]!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TerritorialEntityData.ReadTreeLevelAsync(db, null, null));
    }
}
