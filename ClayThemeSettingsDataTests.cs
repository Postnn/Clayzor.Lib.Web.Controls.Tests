using System.Security.Claims;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Themes;
using Clayzor.Lib.Web.Settings;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class ClayThemeSettingsDataTests
{
    [Fact]
    public async Task ReadsAndWritesOnlyTheSharedThemeParameter()
    {
        const string parameter = "ClayTheme";
        const string value = "modern:light";
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>(
        [
            new() { AffectedRows = 1 },
            Script.Rows(new Dictionary<string, object?> { ["Параметр"] = parameter, ["Значение"] = value }),
        ]));
        using var db = new DbManager("Server=test", null, () => connection);

        await ClayThemeSettingsData.SaveAsync(db, value, CancellationToken.None);

        Assert.Equal(2, connection.CommandLog.Count);
        Assert.StartsWith("INSERT INTO [dbo].[vwНастройки]", connection.CommandLog[0]);
        Assert.StartsWith("SELECT", connection.CommandLog[1]);
        Assert.Contains("FROM [dbo].[vwНастройки]", connection.CommandLog[1]);
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
            "legacy:light", CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("DOMAIN\\SQLservice")]
    public async Task RejectsUntrustedIdentityBeforeOpeningSettingsConnection(string? principal)
    {
        var identity = principal is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim(ClaimTypes.Name, principal)], "Negotiate");
        using var store = new InventoryThemePreferenceStore(() => Task.FromResult(new ClaimsPrincipal(identity)), new ClayAppSettings());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync("modern:light", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("legacy:dark")]
    public async Task RejectsNonCanonicalValueBeforeOpeningSettingsConnection(string value)
    {
        using var store = new InventoryThemePreferenceStore(() => Task.FromResult(new ClaimsPrincipal()), new ClayAppSettings());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(value, CancellationToken.None));
    }
}
