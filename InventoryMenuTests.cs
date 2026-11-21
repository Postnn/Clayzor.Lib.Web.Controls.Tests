using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки URL и прикладной композиции меню Inventory.</summary>
public sealed class InventoryMenuTests
{
    [Fact]
    public void EmptyListForm_UsesInternalIdUrl()
    {
        var link = InventoryMenuUrlBuilder.BuildQueryLink(Query(1001), Config(), "https://inventory/");

        Assert.Equal("/?id=1001", link.Href);
        Assert.Null(link.Error);
        Assert.DoesNotContain("idQuery", link.Href);
    }

    [Fact]
    public void ConfigForm_AddsIdQueryBeforeFragmentAndPreservesQuery()
    {
        var config = Config(new() { ["URI_external"] = "https://forms/search?x=1#anchor" });

        var link = InventoryMenuUrlBuilder.BuildQueryLink(
            Query(1003, "@URI_external"), config, "https://inventory/");

        Assert.Equal("https://forms/search?x=1&idQuery=1003#anchor", link.Href);
        Assert.Null(link.Error);
    }

    [Fact]
    public void RelativeForm_UsesCurrentApplicationAndDoesNotDuplicateIdQuery()
    {
        var link = InventoryMenuUrlBuilder.BuildQueryLink(
            Query(7, "Default/Search.aspx?idQuery=old&x=1#part"), Config(), "https://inventory/root/?id=1001");

        Assert.Equal("https://inventory/root/Default/Search.aspx?x=1&idQuery=7#part", link.Href);
    }

    [Fact]
    public void MissingConfigAndDangerousScheme_AreDisabledWithoutBrokenHref()
    {
        var missing = InventoryMenuUrlBuilder.BuildQueryLink(Query(1, "@URI_missing"), Config(), "https://inventory/");
        var dangerous = InventoryMenuUrlBuilder.BuildQueryLink(
            Query(2, "javascript:alert(1)"), Config(), "https://inventory/");

        Assert.Null(missing.Href);
        Assert.Contains("URI_missing", missing.Error);
        Assert.Null(dangerous.Href);
        Assert.NotNull(dangerous.Error);
    }

    [Fact]
    public void AbsoluteForm_UsesIdQueryNotId()
    {
        var link = InventoryMenuUrlBuilder.BuildQueryLink(
            Query(1003, "https://forms/search"), Config(), "https://inventory/");

        Assert.Equal("https://forms/search?idQuery=1003", link.Href);
        Assert.DoesNotContain("?id=", link.Href);
    }

    [Fact]
    public void ReportAndSettings_KeepTheirDistinctContracts()
    {
        var config = Config(new()
        {
            ["URI_report"] = "https://forms/report?x=1",
            ["URI_inventory_queries"] = "https://inventory/queries?x=1",
        });
        var report = InventoryMenuUrlBuilder.BuildReportLink(
            Query(3065, reportMenu: "Без услуг", reportForm: "@URI_report"), config, "https://inventory/");
        var settings = InventoryMenuUrlBuilder.BuildSettingsLink(config, 1001, "https://inventory/");

        Assert.Equal("https://forms/report?x=1", report.Href);
        Assert.Equal("https://inventory/queries?x=1&_idQuery=1001E", settings.Href);
        Assert.DoesNotContain("idQuery=", report.Href);
    }

    [Fact]
    public void RelativeReportAndSettings_UseActualCurrentUri()
    {
        var config = Config(new()
        {
            ["URI_report"] = "Reports/Current.aspx",
            ["URI_inventory_queries"] = "Admin/Queries.aspx",
        });
        var report = InventoryMenuUrlBuilder.BuildReportLink(
            Query(3065, reportMenu: "Без услуг", reportForm: "@URI_report"), config, "https://inventory/root/?id=3065");
        var settings = InventoryMenuUrlBuilder.BuildSettingsLink(config, 1001, "https://inventory/root/?id=1001");

        Assert.Equal("https://inventory/root/Reports/Current.aspx", report.Href);
        Assert.Equal("https://inventory/root/Admin/Queries.aspx?_idQuery=1001E", settings.Href);
    }

    [Fact]
    public void Composer_PreservesRootsActiveReportAndOmitsOperations()
    {
        var data = new InventoryMenuLoadResult
        {
            Items =
            [
                Query(1003, queryType: 1), Query(1001, queryType: 1), Query(2001, queryType: 2),
                Query(3065, queryType: 3, reportMenu: "Без услуг", reportForm: "@URI_report"),
            ],
            CurrentQuery = Query(3065, queryType: 3, reportMenu: "Без услуг", reportForm: "@URI_report"),
        };
        var config = Config(new()
        {
            ["URI_report"] = "https://forms/report",
            ["URI_inventory_queries"] = "https://inventory/queries",
        });

        var items = InventoryMainMenuComposer.Build(data, 1001, config, "https://inventory/", canOpenSettings: true);

        Assert.Equal(["inventory", "inconsistencies", "reports", "context-report", "settings"], items.Select(item => item.Id));
        Assert.DoesNotContain(items, item => item.Text == "Операции");
        Assert.True(items[0].Children.Single(child => child.Id == "1001").IsActive);
        Assert.Equal("https://forms/report", items[3].Href);
        Assert.EndsWith("_idQuery=1001E", items[4].Href);
    }

    [Fact]
    public void Composer_EmptyRootAndMissingConfigurationRemainExplicit()
    {
        var data = new InventoryMenuLoadResult { Items = [Query(1003, "@URI_missing")] };
        var items = InventoryMainMenuComposer.Build(data, null, Config(), "https://inventory/", canOpenSettings: false);

        Assert.Equal(3, items.Count);
        Assert.True(items[1].Disabled);
        Assert.Empty(items[1].Children);
        var child = Assert.Single(items[0].Children);
        Assert.True(child.Disabled);
        Assert.Null(child.Href);
        Assert.Contains("URI_missing", child.Title);
    }

    [Fact]
    public void DataMap_ReadsOnlyTypedMenuFields()
    {
        var row = new Dictionary<string, object?>
        {
            ["КодЗапроса"] = 3065,
            ["Запрос"] = "SIM",
            ["ТипЗапроса"] = (byte)3,
            ["ФормаСписка"] = DBNull.Value,
            ["Пиктограмма"] = "/styles/phoneGSM.gif",
            ["ПунктМенюОтчета"] = "Без услуг",
            ["ФормаОтчета"] = "@URI_sim_noservices",
        };

        var item = InventoryMenuData.Map(row);

        Assert.Equal(3065, item.Id);
        Assert.Equal(3, item.QueryType);
        Assert.Equal(string.Empty, item.ListForm);
        Assert.Equal("@URI_sim_noservices", item.ReportForm);
    }

    /// <summary>Devadmin допускается к настройкам по имени с доменом и без него.</summary>
    [Theory]
    [InlineData("devadmin", true)]
    [InlineData(@"OTR-AGENT-W-DEV\devadmin", true)]
    [InlineData(@"DOMAIN\DEVADMIN", true)]
    [InlineData(@"DOMAIN\other", false)]
    public void SettingsPermission_DevadminNameIsAllowed(string name, bool expected)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name)], "Negotiate");
        var user = new System.Security.Claims.ClaimsPrincipal(identity);

        Assert.Equal(expected, Kesco.App.Web.Inventory.Components.InventoryMainMenu.CanOpenSettings(user));
    }

    /// <summary>Четыре legacy-группы сохраняют доступ к настройкам через ClaimsPrincipal.</summary>
    [Theory]
    [InlineData(@"BUILTIN\Administrators")]
    [InlineData(@"TEST\Programists")]
    [InlineData(@"EURO\Programists")]
    [InlineData(@"TEST\SupportTeam")]
    public void SettingsPermission_LegacyRolesAreAllowed(string role)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)],
            "Negotiate", System.Security.Claims.ClaimTypes.Name, System.Security.Claims.ClaimTypes.Role);
        var user = new System.Security.Claims.ClaimsPrincipal(identity);

        Assert.True(Kesco.App.Web.Inventory.Components.InventoryMainMenu.CanOpenSettings(user));
    }

    private static InventoryMenuQuery Query(
        int id, string listForm = "", int queryType = 1, string reportMenu = "", string reportForm = "") => new()
    {
        Id = id,
        Caption = $"Запрос {id}",
        QueryType = queryType,
        ListForm = listForm,
        ReportMenuItem = reportMenu,
        ReportForm = reportForm,
    };

    private static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new Dictionary<string, string?>()).Build();
}
