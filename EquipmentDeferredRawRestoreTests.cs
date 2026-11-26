using System.Reflection;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Locations;
using Clayzor.Lib.Web.DBSelect.Persons;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Восстановление raw у обычных дочерних редакторов оборудования.</summary>
public sealed class EquipmentDeferredRawRestoreTests
{
    [Theory]
    [InlineData("location")]
    [InlineData("responsible")]
    [InlineData("computer")]
    [InlineData("software")]
    [InlineData("accounting")]
    public async Task PendingRawBlocksApplyUntilAnExplicitSuccessfulRetry(string kind)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => throw new InvalidOperationException("Неожиданный SQL."));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new EquipmentFormService(db, new TestAuthenticationStateProvider()));
        context.Services.AddSingleton(new EmployeeSelectDataSource(db));
        context.Services.AddSingleton(new LocationSelectDataSource(db));
        context.Services.AddSingleton(new SoftwareEquipmentSelectDataSource(db));
        context.Services.AddSingleton(new AccountingPersonSelectDataSource(db));
        context.Services.AddSingleton(new PersonSelectDataSource(db));
        context.Services.AddKeyedSingleton("accountingReceipt", new AccountingDocumentSelectDataSource(db, writeOff: false));
        context.Services.AddKeyedSingleton("accountingWriteOff", new AccountingDocumentSelectDataSource(db, writeOff: true));
        var dialogs = context.Render<MudDialogProvider>();
        var service = context.Services.GetRequiredService<IDialogService>();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyStates = new List<EquipmentEditorReadiness>();
        var calls = 0;
        var saves = 0;
        var editor = new EquipmentEditorContext(7, 1, () => true)
        {
            RawRestore = () => ++calls == 1 ? pending.Task : Task.CompletedTask,
            ReadinessChanged = state => { readyStates.Add(state); return Task.CompletedTask; },
        };
        var reference = kind switch
        {
            "location" => await service.ShowAsync<EquipmentLocationDialog>("Расположение",
                new DialogParameters<EquipmentLocationDialog> { { dialog => dialog.EditorContext, editor } }),
            "responsible" => await service.ShowAsync<EquipmentResponsibleDialog>("Ответственный",
                new DialogParameters<EquipmentResponsibleDialog> { { dialog => dialog.EditorContext, editor } }),
            "computer" => await service.ShowAsync<EquipmentComputerDialog>("Компьютер",
                new DialogParameters<EquipmentComputerDialog>
                {
                    { dialog => dialog.EditorContext, editor },
                    { dialog => dialog.Save, _ => { saves++; return Task.FromResult(EquipmentEditorSaveResult.Commit()); } },
                }),
            "software" => await service.ShowAsync<EquipmentSoftwareDialog>("ПО",
                new DialogParameters<EquipmentSoftwareDialog> { { dialog => dialog.EditorContext, editor } }),
            _ => await service.ShowAsync<EquipmentAccountingDialog>("Бухгалтерский учёт",
                new DialogParameters<EquipmentAccountingDialog> { { dialog => dialog.EditorContext, editor } }),
        };
        try
        {
            dialogs.WaitForAssertion(() => Assert.Equal(1, calls));
            Assert.True(FindButton(dialogs, "OK").HasAttribute("disabled"));
            Assert.Equal(0, saves);
            if (kind == "computer")
            {
                var computer = dialogs.FindComponent<EquipmentComputerDialog>();
                var apply = typeof(EquipmentComputerDialog).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await computer.InvokeAsync(() => (Task)apply.Invoke(computer.Instance, null)!);
                Assert.Equal(0, saves);
                Assert.False(reference.Result.IsCompleted);
            }
            pending.SetException(new InvalidOperationException("Не все поля присутствуют в DOM."));
            dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindAll("button"),
                button => button.TextContent.Trim() == "Повторить восстановление"));
            Assert.True(FindButton(dialogs, "OK").HasAttribute("disabled"));
            Assert.False(reference.Result.IsCompleted);
            Assert.Equal(0, saves);

            await FindButton(dialogs, "Повторить восстановление").ClickAsync();
            dialogs.WaitForAssertion(() => Assert.False(FindButton(dialogs, "OK").HasAttribute("disabled")));
            Assert.Equal(2, calls);
            Assert.Equal([EquipmentEditorReadiness.Pending, EquipmentEditorReadiness.Failed,
                EquipmentEditorReadiness.Pending, EquipmentEditorReadiness.Ready], readyStates);
            Assert.Equal(0, saves);
            if (kind == "computer")
            {
                await FindButton(dialogs, "OK").ClickAsync();
                Assert.Equal(1, saves);
                Assert.False((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2)))!.Canceled);
            }
        }
        finally { pending.TrySetResult(); }
    }

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<MudDialogProvider> dialogs, string text) =>
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == text);
}
