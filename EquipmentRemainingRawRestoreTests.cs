using System.Reflection;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Components.Pages.Telefon;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Блокировка записи, пока не восстановлен raw оставшихся обычных редакторов.</summary>
public sealed class EquipmentRemainingRawRestoreTests
{
    [Theory]
    [InlineData("phone", "OK")]
    [InlineData("license", "OK")]
    [InlineData("sim-service", "OK")]
    [InlineData("ups-link", "Подключить")]
    public async Task DirectAndButtonApplyRequireSuccessfulRawRestore(string kind, string applyText)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        context.Services.AddKeyedSingleton("ups", new UpsEquipmentSelectDataSource(
            new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection), upsOnly: true));
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var service = context.Services.GetRequiredService<IDialogService>();
        var attempts = 0;
        var writes = 0;
        var dateReads = 0;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new EquipmentEditorContext(7, 1, () => true)
        {
            RawRestore = () => ++attempts == 1 ? pending.Task : Task.CompletedTask,
        };
        Task<EquipmentEditorSaveResult> Save()
        {
            writes++;
            return Task.FromResult(EquipmentEditorSaveResult.Commit());
        }
        var today = new DateTime(2026, 11, 26);
        var reference = kind switch
        {
            "phone" => await service.ShowAsync<EquipmentPhoneDialog>("Срок возврата", new DialogParameters<EquipmentPhoneDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
                { dialog => dialog.Notes, "локальный ввод" },
            }),
            "license" => await service.ShowAsync<EquipmentLicenseDialog>("Лицензия", new DialogParameters<EquipmentLicenseDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
                { dialog => dialog.Types, new LicenseType[] { new() { Id = 1, Name = "Тип", Temporary = 1 } } },
                { dialog => dialog.Value, new EquipmentLicense
                    {
                        LicenseTypeId = 1, ClientCount = 1, ServerCount = 0, LicenseeNumber = "тест",
                        DateFrom = today, DateTo = today.AddDays(1),
                    }
                },
            }),
            "sim-service" => await service.ShowAsync<EquipmentSimServiceDialog>("Услуга", new DialogParameters<EquipmentSimServiceDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
                { dialog => dialog.Value, new EquipmentSimService { ServiceName = "Услуга", DateFrom = today } },
                { dialog => dialog.GetToday, () => { dateReads++; return Task.FromResult(today); } },
            }),
            _ => await service.ShowAsync<EquipmentUpsLinkDialog>("UPS", new DialogParameters<EquipmentUpsLinkDialog>
            {
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
                { dialog => dialog.Value, 9 },
            }),
        };
        AngleSharp.Dom.IElement Button(string text) =>
            dialogs.FindAll("button").Single(button => button.TextContent.Trim() == text);
        try
        {
            dialogs.WaitForAssertion(() => Assert.Equal(1, attempts));
            Assert.True(Button(applyText).HasAttribute("disabled"));
            var child = editor.Component!;
            var apply = child.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await dialogs.InvokeAsync(() => (Task)apply.Invoke(child, null)!);
            Assert.Equal(0, writes);
            Assert.Equal(0, dateReads);
            pending.SetException(new InvalidOperationException("Ввод ещё не восстановлен."));

            dialogs.WaitForAssertion(() => Assert.NotNull(Button("Повторить восстановление")));
            Assert.True(Button(applyText).HasAttribute("disabled"));
            Assert.False(reference.Result.IsCompleted);
            await Button("Повторить восстановление").ClickAsync();
            dialogs.WaitForAssertion(() => Assert.False(Button(applyText).HasAttribute("disabled")));
            Assert.Equal(2, attempts);
            Assert.Equal(0, writes);
            await Button(applyText).ClickAsync();
            Assert.False((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2)))!.Canceled);
            Assert.Equal(1, writes);
            Assert.Equal(kind == "sim-service" ? 1 : 0, dateReads);
        }
        finally { pending.TrySetResult(); }
    }

    [Fact]
    public async Task PhoneReturnUnderTelKeepsItsIndependentRestorePath()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        const string scope = "tel.return.fixture";
        var restore = new ClayDraftRestore("tel-fixture", true, 5, null, null);
        context.JSInterop.Setup<ClayDraftRestore>("clayDraftStore.register", _ => true).SetResult(restore);
        var drafts = new ClayDraftService(context.JSInterop.JSRuntime);
        await using var registration = await drafts.RegisterHandleAsync("Tel", "7", "fixture-user", 1);
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", call => Equals(call.Arguments[1], scope))
            .SetResult(new ClayDraftRawApplyResult([], ["data:phone"]));
        var original = new EquipmentPhoneReturnResult(null, "исходный ввод");
        var originalDate = new DateTime(2026, 11, 26);
        var typed = JsonSerializer.Serialize(new { Version = 1, Original = original, Current = original, OriginalChangedAtUtc = originalDate });
        var accepted = 0;
        var session = new TelRelationDraftSession<EquipmentPhoneReturnResult>(registration, scope, "fixture-root", default,
            () => true, _ => { }, _ => { accepted++; return Task.FromResult(1L); }, typed, true, original, originalDate);
        var dialogs = context.Render<MudDialogProvider>();
        var reference = await context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<EquipmentPhoneDialog>("Срок возврата", new DialogParameters<EquipmentPhoneDialog>
            {
                { dialog => dialog.TelDraft, session },
                { dialog => dialog.Notes, original.Notes },
            });
        dialogs.WaitForAssertion(() => Assert.Contains("Повторите восстановление.", dialogs.Markup));
        Assert.True(dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.DoesNotContain("Не все поля восстановлены. Сохранение заблокировано.", dialogs.Markup);
        Assert.False(reference.Result.IsCompleted);
        Assert.Equal(0, accepted);
    }
}
