using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class ClayThemeSettingsDataTests
{
    [Theory]
    [InlineData(ClayThemeSettingsData.InventoryParameter)]
    [InlineData(ClayThemeSettingsData.UsersParameter)]
    public async Task WritesAndVerifiesOnlyTheApplicationParameter(string parameter)
    {
        const string value = "modern:light";
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>(
        [
            new() { AffectedRows = 1 },
            Script.Rows(new Dictionary<string, object?> { ["Параметр"] = parameter, ["Значение"] = value }),
        ]));
        using var db = new DbManager("Server=test", null, () => connection);

        await ClayThemeSettingsData.SaveAsync(db, parameter, value, CancellationToken.None);

        Assert.Equal(2, connection.CommandLog.Count);
        Assert.StartsWith("INSERT INTO [vwНастройки]", connection.CommandLog[0]);
        Assert.StartsWith("SELECT", connection.CommandLog[1]);
        Assert.Equal(parameter, connection.ParameterLog[0]["name"]);
        Assert.Equal(value, connection.ParameterLog[0]["value"]);
        Assert.Equal(parameter, connection.ParameterLog[1]["n0"]);
        Assert.All(connection.ParameterLog, args =>
        {
            Assert.Equal(0, args["clid"]);
            Assert.Equal(0, args["shid"]);
        });
    }

    [Fact]
    public async Task SilentTriggerRejectionDoesNotReportSuccess()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>(
        [
            new() { AffectedRows = 0 },
            Script.Rows(),
        ]));
        using var db = new DbManager("Server=test", null, () => connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ClayThemeSettingsData.SaveAsync(db,
            ClayThemeSettingsData.InventoryParameter, "legacy:light", CancellationToken.None));
    }
}
