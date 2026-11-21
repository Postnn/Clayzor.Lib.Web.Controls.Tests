using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using System.Reflection;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки добавления сервера и немедленного создания UPS-теста.</summary>
public sealed class EquipmentUpsDialogTests
{
    /// <summary>Новый компьютер сохраняет сетевое имя и доступен в тесте до сохранения связи.</summary>
    [Fact]
    public async Task NewServer_IsImmediatelySelectableInTestWithNetworkName()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 9), ("НазваниеОборудования", "Компьютер srv-new"),
                ("КодТипаОборудования", 1), ("НазваниеСервера", "srv-new"))),
        ]));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        _ = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentUpsDialog>(
            "UPS", new DialogParameters<EquipmentUpsDialog>
            {
                { dialog => dialog.UpsEquipmentId, 10 },
                { dialog => dialog.Servers, new List<UpsServerLink>() },
                { dialog => dialog.Tests, new List<UpsTest>() },
            });
        var dialog = provider.FindComponent<EquipmentUpsDialog>();
        SetPrivate(dialog.Instance, "_serverToAdd", (int?)9);

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Добавить").ClickAsync();

        dialog.WaitForAssertion(() => Assert.Single(dialog.Instance.Servers));
        Assert.Equal("srv-new", dialog.Instance.Servers[0].ServerName);
        Assert.Equal(1, dialog.Instance.Servers[0].EquipmentTypeId);
        var addTest = typeof(EquipmentUpsDialog).GetMethod("AddTest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task? pending = null;
        await dialog.InvokeAsync(() =>
        {
            pending = (Task)addTest.Invoke(dialog.Instance, null)!;
        });
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<EquipmentUpsTestDialog>()));
        var testDialog = provider.FindComponent<EquipmentUpsTestDialog>();
        testDialog.WaitForAssertion(() => Assert.Contains("srv-new", testDialog.Markup));
        var row = testDialog.FindAll("tbody tr").Single(item => item.TextContent.Contains("srv-new"));
        Assert.False(row.QuerySelector(".clay-checkbox")!.HasAttribute("aria-disabled"));
        await row.QuerySelector(".clay-checkbox")!.ClickAsync();
        await testDialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        dialog.WaitForAssertion(() => Assert.Single(dialog.Instance.Tests));
        Assert.Equal("srv-new", dialog.Instance.Tests[0].Servers);
        await pending!;
    }

    private static BunitContext CreateContext(ScriptedConnection connection)
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
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), false));
        return context;
    }

    private static void SetPrivate(EquipmentUpsDialog dialog, string name, object? value) =>
        typeof(EquipmentUpsDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(dialog, value);
}
