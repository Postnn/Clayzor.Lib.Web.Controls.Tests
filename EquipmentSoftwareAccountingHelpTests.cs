using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Persons;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет справку и тему редакторов установленного ПО и бухгалтерского учёта.</summary>
public sealed class EquipmentSoftwareAccountingHelpTests
{
    [Theory]
    [InlineData(false, "InstallSoft.aspx")]
    [InlineData(true, "EquipmentBuxMove_form.aspx")]
    public async Task ThemeKeepsLocalEditsAndDoesNotDispatch(bool accounting, string legacyPage)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => throw new InvalidOperationException("Эта проверка не выполняет запросы."));
        context.Services.AddSingleton(new SoftwareEquipmentSelectDataSource(db));
        context.Services.AddSingleton(new AccountingPersonSelectDataSource(db));
        context.Services.AddSingleton(new PersonSelectDataSource(db));
        context.Services.AddKeyedSingleton("accountingReceipt", new AccountingDocumentSelectDataSource(db, writeOff: false));
        context.Services.AddKeyedSingleton("accountingWriteOff", new AccountingDocumentSelectDataSource(db, writeOff: true));
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var dialogService = context.Services.GetRequiredService<IDialogService>();
        var writes = 0;
        var publications = 0;
        var applies = 0;
        var editor = new EquipmentEditorContext(7, 1, () => true)
        {
            DraftChanged = _ => { publications++; return Task.CompletedTask; },
            RawRestore = () => { applies++; return Task.CompletedTask; },
        };
        Task<EquipmentEditorSaveResult> Save()
        {
            writes++;
            return Task.FromResult(EquipmentEditorSaveResult.Commit());
        }
        var reference = accounting
            ? await dialogService.ShowAsync<EquipmentAccountingDialog>("Бухгалтерский учёт", new DialogParameters<EquipmentAccountingDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            })
            : await dialogService.ShowAsync<EquipmentSoftwareDialog>("Установленное ПО", new DialogParameters<EquipmentSoftwareDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            });
        const string notes = "Несохранённое примечание";
        await dialogs.Find("textarea").InputAsync(notes);
        var state = editor.CaptureDraftState();
        Assert.Equal(notes, dialogs.FindComponent<ClayTextArea>().Instance.Value);
        var publicationsBefore = publications;
        var appliesBefore = applies;

        await EquipmentPeriodDialogHelpTests.CheckHelpAndChangeThemeAsync(context, dialogs, popovers, legacyPage);

        Assert.Equal(notes, dialogs.FindComponent<ClayTextArea>().Instance.Value);
        Assert.Equal(state, editor.CaptureDraftState());
        Assert.Equal(publicationsBefore, publications);
        Assert.Equal(appliesBefore, applies);
        Assert.True(editor.IsCurrent);
        Assert.False(reference.Result.IsCompleted);
        Assert.Equal(0, writes);
    }
}
