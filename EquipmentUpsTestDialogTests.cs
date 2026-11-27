using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки отрисованного выбора серверов и подтверждения UPS-теста.</summary>
public sealed class EquipmentUpsTestDialogTests
{
    /// <summary>Новый/существующий тест показывает только доступные компьютеры и read-only исторические имена.</summary>
    [Fact]
    public async Task ExistingTest_PreservesHistoricalNamesAndWarnsAboutUnchecked()
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var value = ExistingValue();
        var reference = await ShowAsync(context, value);
        var dialog = provider.FindComponent<EquipmentUpsTestDialog>();

        dialog.WaitForAssertion(() => Assert.Contains("legacy", dialog.Markup));
        Assert.DoesNotContain("not-computer", dialog.Markup);
        Assert.Contains("srv-b", dialog.Markup);
        Assert.True(dialog.FindAll("tbody tr").Single(row => row.TextContent.Contains("legacy"))
            .QuerySelector(".clay-checkbox")!.HasAttribute("aria-disabled"));
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));

        var updated = Assert.IsType<UpsTest>(result.Data);
        Assert.Equal("srv-a,legacy", updated.Servers);
        Assert.Equal("Сервера, не получившие уведомление: srv-b", updated.Issues);
    }

    /// <summary>Отметка сервера через реальный чекбокс убирает предупреждение и попадает в результат.</summary>
    [Fact]
    public async Task ToggleServer_UpdatesSelectionWithoutWarning()
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var value = ExistingValue();
        var reference = await ShowAsync(context, value);
        var dialog = provider.FindComponent<EquipmentUpsTestDialog>();
        var row = dialog.FindAll("tbody tr").Single(item => item.TextContent.Contains("srv-b"));

        await row.QuerySelector(".clay-checkbox")!.ClickAsync();
        dialog.WaitForAssertion(() => Assert.DoesNotContain("выбраны не все сервера", dialog.Markup));
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));

        var updated = Assert.IsType<UpsTest>(result.Data);
        Assert.Equal("srv-a,legacy,srv-b", updated.Servers);
        Assert.Equal(string.Empty, updated.Issues);
    }

    /// <summary>Превышение длины Issues блокирует OK; последующая отмена не меняет исходное значение.</summary>
    [Fact]
    public async Task IssuesLengthLimit_BlocksApplyAndCancelLeavesValue()
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var value = ExistingValue();
        value.Issues = new string('x', 4000);
        var reference = await ShowAsync(context, value);
        var dialog = provider.FindComponent<EquipmentUpsTestDialog>();

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        Assert.Contains("Не удалось дописать", dialog.Markup);
        Assert.False(reference.Result.IsCompleted);
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync();
        Assert.True((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2))).Canceled);
        Assert.Equal("srv-a,legacy", value.Servers);
        Assert.Equal(4000, value.Issues.Length);
    }

    /// <summary>SQL row проходит через настоящее клонирование формы в активный выбор сервера.</summary>
    [Fact]
    public async Task SqlRowThroughFormClone_PreservesComputerSelection()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(
                Script.Row(("КодОборудованияUPS", 10), ("КодОборудованияСервера", 1),
                    ("НастроеноВыключение", (byte)0), ("НазваниеСервера", "srv-a"), ("КодТипаОборудования", 1)),
                Script.Row(("КодОборудованияUPS", 10), ("КодОборудованияСервера", 2),
                    ("НастроеноВыключение", (byte)0), ("НазваниеСервера", "not-computer"), ("КодТипаОборудования", 2))),
        ]));
        var service = new Kesco.App.Web.Inventory.Services.EquipmentFormService(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        var loaded = await service.GetUpsServersAsync(10);
        var clone = typeof(Kesco.App.Web.Inventory.Components.Pages.Equipment.EquipmentForm)
            .GetMethod("CloneUpsServers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var servers = (List<UpsServerLink>)clone.Invoke(null, [loaded])!;

        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var value = ExistingValue();
        value.Servers = "legacy";
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentUpsTestDialog>(
            "UPS test", new DialogParameters<EquipmentUpsTestDialog>
            {
                { dialog => dialog.Value, value },
                { dialog => dialog.Date, value.TestedAt.Date },
                { dialog => dialog.Time, value.TestedAt.TimeOfDay },
                { dialog => dialog.AvailableServers, (IReadOnlyList<UpsServerLink>)servers },
            });
        var dialog = provider.FindComponent<EquipmentUpsTestDialog>();

        dialog.WaitForAssertion(() => Assert.Contains("srv-a", dialog.Markup));
        Assert.DoesNotContain("not-computer", dialog.Markup);
        var row = dialog.FindAll("tbody tr").Single(item => item.TextContent.Contains("srv-a"));
        Assert.False(row.QuerySelector(".clay-checkbox")!.HasAttribute("aria-disabled"));
        await row.QuerySelector(".clay-checkbox")!.ClickAsync();
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));

        var updated = Assert.IsType<UpsTest>(result.Data);
        Assert.Equal("legacy,srv-a", updated.Servers);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddScoped<DbManager>(_ => new DbManager("Server=test"));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton(new ClayFormWindowService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton<NavigationManager>(new FakeNavigationManager());
        return context;
    }

    /// <summary>Сброс вложенного теста применяет только подтверждённую ревизию.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopedResetKeepsNestedInputWhenRevisionChanges(bool acceptsRevision)
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var original = ExistingValue();
        var local = ExistingValue();
        local.BatteryTime = 11;
        var fresh = ExistingValue();
        fresh.BatteryTime = 20;
        int reads = 0;
        int writes = 0;
        var cancellation = new EquipmentUpsTestCancellation(
            () => Task.FromResult<ClayDraftEditorScopeState?>(new(12, false)),
            () =>
            {
                reads++;
                return Task.FromResult<EquipmentUpsTestDraft?>(new(fresh, fresh.TestedAt.Date, fresh.TestedAt.TimeOfDay,
                    fresh.BatteryTime, fresh.Servers, fresh.Issues));
            },
            (revision, replacement, current) =>
            {
                writes++;
                Assert.Equal(12L, revision);
                Assert.Equal(20, replacement?.BatteryTime);
                Assert.True(current());
                return Task.FromResult(acceptsRevision);
            });
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentUpsTestDialog>(
            "UPS test", new DialogParameters<EquipmentUpsTestDialog>
            {
                { dialog => dialog.Original, original },
                { dialog => dialog.Value, local },
                { dialog => dialog.Date, local.TestedAt.Date },
                { dialog => dialog.Time, local.TestedAt.TimeOfDay },
                { dialog => dialog.ScopedCancellation, cancellation },
            });
        var editor = provider.FindComponent<EquipmentUpsTestDialog>();

        var cancel = editor.InvokeAsync(() =>
            editor.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<Clayzor.Lib.Web.Controls.Components.Form.ClayEditorCancelDialog>()));
        await provider.InvokeAsync(() => provider.FindComponent<Clayzor.Lib.Web.Controls.Components.Form.ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, reads);
        Assert.Equal(1, writes);
        Assert.Equal(acceptsRevision ? 20 : 11, editor.Instance.Value.BatteryTime);
        Assert.Same(acceptsRevision ? fresh : original, editor.Instance.Original);
        Assert.False(reference.Result.IsCompleted);
    }

    private static UpsTest ExistingValue() => new()
    {
        Id = 5,
        UpsEquipmentId = 10,
        TestedAt = new DateTime(2026, 11, 20, 8, 30, 0),
        BatteryTime = 10,
        Servers = "srv-a,legacy",
        Issues = string.Empty,
    };

    private static async Task<IDialogReference> ShowAsync(BunitContext context, UpsTest value) =>
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentUpsTestDialog>(
            "UPS test", new DialogParameters<EquipmentUpsTestDialog>
            {
                { dialog => dialog.Value, value },
                { dialog => dialog.Date, value.TestedAt.Date },
                { dialog => dialog.Time, value.TestedAt.TimeOfDay },
                { dialog => dialog.AvailableServers, (IReadOnlyList<UpsServerLink>)
                    [
                        new UpsServerLink { ServerEquipmentId = 1, ServerName = "srv-a", EquipmentTypeId = 1 },
                        new UpsServerLink { ServerEquipmentId = 2, ServerName = "srv-b", EquipmentTypeId = 1 },
                        new UpsServerLink { ServerEquipmentId = 3, ServerName = "not-computer", EquipmentTypeId = 2 },
                        new UpsServerLink { ServerEquipmentId = 4, ServerName = "", EquipmentTypeId = 1 },
                    ] },
            });
}
