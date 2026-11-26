using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет владельца справки у общих и доступных только для чтения диалогов.</summary>
public sealed class EquipmentPhoneHistoryHelpTests
{
    [Theory]
    [InlineData("/Equipment/EquipmentForm?id=7#phones", "Equipment%2FEquipment_form.aspx")]
    [InlineData("/Telefon/Tel.aspx?id=5#equipment", "Telefon%2FTel.aspx")]
    public async Task ReturnEditorUsesItsOwnerAndKeepsNotes(string location, string path)
    {
        await using var context = Configure(location);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var service = context.Services.GetRequiredService<IDialogService>();
        var editor = new EquipmentEditorContext(7, 1, () => true);
        var applies = 0;
        var publications = 0;
        editor.RawRestore = () => { applies++; return Task.CompletedTask; };
        editor.DraftChanged = _ => { publications++; return Task.CompletedTask; };
        var reference = await service.ShowAsync<EquipmentPhoneDialog>("Срок возврата", new DialogParameters<EquipmentPhoneDialog>
        {
            { dialog => dialog.EditorContext, editor },
            { dialog => dialog.ReturnDate, new DateTime(2026, 12, 1) },
            { dialog => dialog.Save, _ => throw new InvalidOperationException("Неожиданное сохранение") },
        });
        var dialog = dialogs.FindComponent<EquipmentPhoneDialog>();
        await dialog.FindComponent<ClayTextBox>().Find("input").InputAsync("Несохранённое примечание");
        var before = editor.CaptureDraftState();
        var publishedBefore = publications;
        var appliedBefore = applies;
        Assert.Equal("https://docs.example/wiki?topic=Inventory%2F" + path,
            dialog.Find("a[aria-label='Документация']").GetAttribute("href"));
        await SwitchThemeAsync(dialogs, popovers);
        Assert.Equal("Несохранённое примечание", dialog.Instance.Notes);
        Assert.Equal(new DateTime(2026, 12, 1), dialog.Instance.ReturnDate);
        Assert.Equal(before, editor.CaptureDraftState());
        Assert.Equal(publishedBefore, publications);
        Assert.Equal(appliedBefore, applies);
        Assert.False(reference.Result.IsCompleted);
    }

    [Fact]
    public async Task ReturnEditorUnderUnrelatedRouteDoesNotUseItsWikiIdentity()
    {
        await using var context = Configure("/Maintenance.aspx");
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentPhoneDialog>("Срок возврата");
        Assert.Contains("Документация формы недоступна", dialogs.Markup);
        Assert.Empty(dialogs.FindAll("a[aria-label='Документация']"));
    }

    [Fact]
    public async Task HistoryUsesEquipmentOwnerAndRemainsOpen()
    {
        await using var context = Configure("/Equipment/EquipmentForm?id=7");
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var reference = await context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<EquipmentNetworkNameHistoryDialog>("История", new DialogParameters<EquipmentNetworkNameHistoryDialog>
            {
                { dialog => dialog.Items, new EquipmentNetworkNameHistory[] { new() { NetworkName = "fixture-server" } } },
            });
        await EquipmentPeriodDialogHelpTests.CheckHelpAndChangeThemeAsync(context, dialogs, popovers, "Equipment_form.aspx");
        Assert.Contains("fixture-server", dialogs.Markup);
        Assert.False(reference.Result.IsCompleted);
    }

    private static async Task SwitchThemeAsync(IRenderedComponent<MudDialogProvider> dialogs, IRenderedComponent<MudPopoverProvider> popovers)
    {
        await dialogs.Find("button[aria-label='Тема оформления']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await popovers.WaitForElement("[data-clay-theme-option='modern:light']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    private static BunitContext Configure(string location)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost" + location);
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new ClayFormWindowService(context.JSInterop.JSRuntime));
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddSingleton(new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => throw new InvalidOperationException("Эта проверка не выполняет запросы.")));
        return context;
    }
}
