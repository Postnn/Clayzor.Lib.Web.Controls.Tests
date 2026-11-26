using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет справку и тему редакторов SIM, услуг и лицензий.</summary>
public sealed class EquipmentSimLicenseHelpTests
{
    [Theory]
    [InlineData("SIM_form.aspx", "sim.phone", "79000000009")]
    [InlineData("AdditionalService_form.aspx", "sim-service.name", "Дополнительная услуга")]
    [InlineData("Soft_form.aspx", "license.distribution", "Тестовый дистрибутив")]
    public async Task ThemeKeepsEditorAndLocalDraft(string legacyPage, string draftKey, string text)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => throw new InvalidOperationException("Эта проверка не выполняет запросы."));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new EquipmentFormService(db, new TestAuthenticationStateProvider()));
        context.Services.AddScoped<Clayzor.Lib.Web.Controls.Services.ClayDraftService>();
        context.Services.AddSingleton(new MobileContractSelectDataSource(db));
        context.Services.AddSingleton(new PersonalDataPersonSelectDataSource(db));
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var service = context.Services.GetRequiredService<IDialogService>();
        var publications = 0;
        var rawApplies = 0;
        var writes = 0;
        var editor = new EquipmentEditorContext(7, 1, () => true)
        {
            DraftChanged = _ => { publications++; return Task.CompletedTask; },
            RawRestore = () => { rawApplies++; return Task.CompletedTask; },
        };
        Task<EquipmentEditorSaveResult> Save()
        {
            writes++;
            return Task.FromResult(EquipmentEditorSaveResult.Commit());
        }
        var reference = draftKey switch
        {
            "sim.phone" => await service.ShowAsync<EquipmentSimDialog>("SIM", new DialogParameters<EquipmentSimDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            }),
            "sim-service.name" => await service.ShowAsync<EquipmentSimServiceDialog>("Услуга", new DialogParameters<EquipmentSimServiceDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            }),
            _ => await service.ShowAsync<EquipmentLicenseDialog>("Лицензия", new DialogParameters<EquipmentLicenseDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            }),
        };
        var field = dialogs.FindComponents<ClayTextBox>().Single(component => component.Instance.Options?.DraftKey == draftKey);
        await field.Find("input").InputAsync(text);
        Assert.Equal(text, field.Instance.Value);
        var state = editor.CaptureDraftState();
        Assert.NotNull(state);
        var publicationsBefore = publications;
        var rawBefore = rawApplies;

        await EquipmentPeriodDialogHelpTests.CheckHelpAndChangeThemeAsync(context, dialogs, popovers, legacyPage);

        Assert.Equal(text, field.Instance.Value);
        Assert.Equal(state, editor.CaptureDraftState());
        Assert.Equal(publicationsBefore, publications);
        Assert.Equal(rawBefore, rawApplies);
        Assert.True(editor.IsCurrent);
        Assert.False(reference.Result.IsCompleted);
        Assert.Equal(0, writes);
    }
}
