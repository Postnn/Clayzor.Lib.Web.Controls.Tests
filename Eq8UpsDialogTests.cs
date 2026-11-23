using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки UPS dialog applicability и audit EQ8-11/H52.</summary>
public sealed class Eq8UpsDialogTests
{
    /// <summary>Noncomputer flag read-only, unnamed display сохранён, audit обеих таблиц виден.</summary>
    [Fact]
    public async Task Dialog_ShowsAuditAndProtectsHistoricalNonComputerFlag()
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var servers = new List<UpsServerLink>
        {
            new() { ServerEquipmentId = 1, EquipmentTypeId = 1, ServerName = "srv-a", EquipmentName = "Компьютер A" },
            new() { ServerEquipmentId = 2, EquipmentTypeId = 1, ServerName = "", EquipmentName = "Компьютер без имени" },
            new() { ServerEquipmentId = 3, EquipmentTypeId = 2, ServerName = "printer", EquipmentName = "Принтер", TurnOffConfigured = 1 },
        };
        SetAudit(servers[0], "Server Editor", new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc));
        var tests = new List<UpsTest>
        {
            new()
            {
                Id = 5,
                TestedAt = new DateTime(2026, 11, 20, 9, 30, 0),
                ChangedByName = "Test Editor",
                ChangedAt = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc),
            },
        };
        _ = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentUpsDialog>(
            "UPS", new DialogParameters<EquipmentUpsDialog>
            {
                { dialog => dialog.UpsEquipmentId, 10 },
                { dialog => dialog.Servers, servers },
                { dialog => dialog.Tests, tests },
            });
        var dialog = provider.FindComponent<EquipmentUpsDialog>();

        dialog.WaitForAssertion(() => Assert.Contains("Компьютер без имени", dialog.Markup));
        var historicalRow = dialog.FindAll("tbody tr").Single(row => row.TextContent.Contains("printer"));
        Assert.True(historicalRow.QuerySelector(".clay-checkbox")!.HasAttribute("aria-disabled"));
        Assert.Contains("только для компьютеров", dialog.Markup);
        Assert.Contains("Server Editor", dialog.Markup);
        Assert.Contains("Test Editor", dialog.Markup);
        Assert.Contains("Изменил", dialog.Markup);
        Assert.Contains("Изменено", dialog.Markup);
        var computerRow = dialog.FindAll("tbody tr").Single(row => row.TextContent.Contains("srv-a"));
        Assert.False(computerRow.QuerySelector(".clay-checkbox")!.HasAttribute("aria-disabled"));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddScoped<DbManager>(_ => new DbManager("Server=test"));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton(new ClayFormWindowService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton<NavigationManager>(new FakeNavigationManager());
        context.Services.AddKeyedSingleton("server", new UpsEquipmentSelectDataSource(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>())), false));
        return context;
    }

    private static void SetAudit(UpsServerLink server, string changedBy, DateTime changedAt)
    {
        server.GetType().GetProperty("ChangedByName")?.SetValue(server, changedBy);
        server.GetType().GetProperty("ChangedAt")?.SetValue(server, changedAt);
    }
}
