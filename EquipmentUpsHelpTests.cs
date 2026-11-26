using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Справка и тема разных бизнес-форм подключения и тестирования UPS.</summary>
public sealed class EquipmentUpsHelpTests
{
    [Theory]
    [InlineData("editor", "LinkUps.aspx")]
    [InlineData("link", "Equipment_form.aspx")]
    [InlineData("test", "LinkUps.aspx")]
    public async Task ThemeKeepsTheUpsEditorOpenAndDoesNotSendCommands(string kind, string legacyPage)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => throw new InvalidOperationException("Эта проверка не выполняет запросы."));
        context.Services.AddSingleton(db);
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddSingleton(new ClayFormWindowService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton<NavigationManager>(new FakeNavigationManager());
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddKeyedSingleton("ups", new UpsEquipmentSelectDataSource(db, upsOnly: true));
        context.Services.AddKeyedSingleton("server", new UpsEquipmentSelectDataSource(db, upsOnly: false));
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var service = context.Services.GetRequiredService<IDialogService>();
        var editor = new EquipmentEditorContext(7, 1, () => true);
        var rawApplies = 0;
        var publications = 0;
        var writes = 0;
        editor.RawRestore = () => { rawApplies++; return Task.CompletedTask; };
        editor.DraftChanged = _ => { publications++; return Task.CompletedTask; };
        Task<EquipmentEditorSaveResult> Save()
        {
            writes++;
            return Task.FromResult(EquipmentEditorSaveResult.Unknown("Исход требует сверки."));
        }
        var result = kind switch
        {
            "link" => await service.ShowAsync<EquipmentUpsLinkDialog>("Подключение UPS", new DialogParameters<EquipmentUpsLinkDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            }),
            "test" => await service.ShowAsync<EquipmentUpsTestDialog>("Тестирование UPS"),
            _ => await service.ShowAsync<EquipmentUpsDialog>("Подключение UPS", new DialogParameters<EquipmentUpsDialog>
            {
                { dialog => dialog.UpsEquipmentId, 10 },
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            }),
        };
        const string issues = "Несохранённые замечания";
        if (kind == "test")
            await dialogs.Find("textarea").InputAsync(issues);
        if (kind == "editor")
            await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        var state = editor.CaptureDraftState();
        var originalPublications = publications;
        var originalRawApplies = rawApplies;
        var originalWrites = writes;

        await EquipmentPeriodDialogHelpTests.CheckHelpAndChangeThemeAsync(context, dialogs, popovers, legacyPage);

        if (kind == "test")
            Assert.Equal(issues, dialogs.FindComponent<ClayTextArea>().Instance.Value);
        if (kind == "link")
            Assert.True(dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Подключить").HasAttribute("disabled"));
        if (kind == "editor")
        {
            Assert.Contains("Исход требует сверки.", dialogs.Markup);
            Assert.True(dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        }
        Assert.Equal(state, editor.CaptureDraftState());
        Assert.Equal(originalPublications, publications);
        Assert.Equal(originalRawApplies, rawApplies);
        Assert.False(result.Result.IsCompleted);
        Assert.Equal(kind == "editor" ? 1 : 0, originalWrites);
        Assert.Equal(originalWrites, writes);
    }
}
