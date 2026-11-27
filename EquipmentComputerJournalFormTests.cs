using System.Reflection;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>После потери ответа сверяет исходную дату, не отправляет запись повторно и сохраняет поздний ввод.</summary>
    [Fact]
    public async Task ComputerLostReplyReconcilesTheOriginalCalendarDay()
    {
        var fixture = CreateFixture(computer: true);
        fixture.Service.ComputerSaveCompletion.SetException(new EquipmentOperationException(EquipmentWritePhase.Unknown,
            "Ответ записи потерян.", new TimeoutException()));
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["clayDraftStore.registerRam"]));
        SetActiveChildDraft(page.Instance, nameof(EquipmentComputerDialog), null);
        var opening = typeof(EquipmentForm).GetMethod("CaptureEditorOpening", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page.Instance, null)!;
        var day = new DateTime(2026, 11, 18);
        var snapshot = new EquipmentComputerSnapshot { Date = day.AddHours(13), Notes = "Значения снимка", RamComponents = [1024] };

        await InvokeHandler(page, "SaveComputerDraftAsync", snapshot, opening, CurrentModel(page.Instance).ModelId);

        var attempt = fixture.Service.ComputerWriteAttempt;
        Assert.NotEqual(Guid.Empty, attempt);
        Assert.Equal(day, fixture.Service.ComputerWriteDate);
        Assert.NotNull(fixture.Service.ComputerWriteBusinessDay);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var browser = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.Equal(attempt.ToString("N"), browser.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal("child:EquipmentComputerDialog:new:root", browser.RootElement.GetProperty("acceptedScopes")[0].GetString());
        using var data = ReadChildOperationEnvelope(context);
        var intent = data.RootElement.GetProperty("Intent");
        Assert.Equal("20261118", intent.GetProperty("Date").GetString());
        Assert.Single(intent.EnumerateObject());

        fixture.Service.ComputerWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 8);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        FindSn(page).Input("CLAY_TEST_COMPUTER_LATER");
        fixture.Service.ComputerWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 7);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Equal(day, fixture.Service.ComputerReadDate);
        Assert.Equal(attempt, fixture.Service.ComputerReadAttempt);
        Assert.Equal(1, fixture.Service.ComputerSaveCalls);
        Assert.Equal(1, fixture.Service.ComputerRefreshCalls);
        Assert.Equal("CLAY_TEST_COMPUTER_LATER", FindSn(page).GetAttribute("value"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }
}
