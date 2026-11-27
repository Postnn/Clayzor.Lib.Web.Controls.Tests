using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.TerritorialEntity;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.WebUtilities;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class PhoneCodeEntityTests
{
    [Theory]
    [InlineData("00+12%&", 1)]
    [InlineData("00123", 255)]
    public void StringKeyAndZeroTypeAreAllowedAndUrlEncodesOnce(string code, int length)
    {
        Assert.Empty(TerritorialEntityRules.Validate(new PhoneCodeEntityRecord
        {
            Code = code, Length = length, TerritoryId = 1, TypeId = 0,
        }));
        var route = TerritorialEntityRoutes.PhoneEdit(code);
        Assert.Equal(code, QueryHelpers.ParseQuery(route[route.IndexOf('?')..])["id"].ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("256")]
    [InlineData("2.1")]
    [InlineData("2,1")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public void AreaLengthRejectsFractionsAndOutOfRangeValues(string value) =>
        Assert.False(TerritorialEntityRules.TryParseAreaLength(value, out _));

    [Fact]
    public async Task SearchKeepsDigitCodeSeparateFromNamesAndEscapesLiteralWildcards()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(), Script.Rows()]));
        using var db = new DbManager("Server=test", null, () => connection);
        await TerritorialEntityData.SearchPhoneTerritoriesAsync(db, "0061", 10);
        Assert.Contains("ТелКодСтраны=@CountryCode", connection.CommandLog[0]);
        Assert.Contains("КодТТерритории IN", connection.CommandLog[0]);
        Assert.Equal("0061", connection.ParameterLog[0]["CountryCode"]);

        await TerritorialEntityData.SearchPhoneTerritoriesAsync(db, "Австралия \"Syd% Bay\"", 10);
        Assert.Contains("((Территория LIKE @word0 AND Территория LIKE @word1) OR (Caption LIKE @word0 AND Caption LIKE @word1))", connection.CommandLog[1]);
        Assert.Equal("%Австралия%", connection.ParameterLog[1]["word0"]);
        Assert.Equal("%Syd[%] Bay%", connection.ParameterLog[1]["word1"]);
        Assert.Throws<ArgumentException>(() => { _ = TerritorialEntityData.SearchPhoneTerritoriesAsync(db, new string('9', 21), 10); });
        Assert.Equal(2, connection.CommandLog.Count);
    }

    [Fact]
    public async Task ExistingKeyCannotBeChangedBeforeAnyDatabaseOperation()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>());
        using var db = new DbManager("Server=test", null, () => connection);
        await Assert.ThrowsAsync<TerritorialEntityRuleException>(() => TerritorialEntityData.SavePhoneAsync(db, "DOMAIN\\Test",
            4, new PhoneCodeEntityRecord { Code = "001" }, new PhoneCodeEntityRecord { Code = "1" }));
        Assert.Empty(connection.CommandLog);
    }
}
