using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Обычная запись оставляет локальный журнал до успешной фиксации известного исхода.</summary>
    [Fact]
    public async Task MainWriteRetriesConfirmationWithoutRepeatingUpdate()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var failRecording = true;
        context.JSInterop.SetupVoid("clayDraftStore.recordConfirmedOperation", _ => failRecording)
            .SetException(new JSException("Тестовый отказ записи подтверждения."));
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_NEW");

        await InvokeSave(page, false);

        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        failRecording = false;
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        var cleanup = Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        var confirmations = context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"];
        Assert.Equal(2, confirmations.Count);
        Assert.All(confirmations, call => Assert.Equal(cleanup.Arguments[1], call.Arguments[1]));
        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.False(PrivateBool(page.Instance, "_draftOperationUnresolved"));
    }

    /// <summary>Восстановленная отметка допускает очистку только после чтения точной серверной квитанции.</summary>
    [Fact]
    public async Task MainWriteWithRestoredConfirmedMarkerRequiresServerReceipt()
    {
        var fixture = CreateFixture();
        var id = Guid.NewGuid();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["clayDraftStore.registerRam"]));
        SetPrivate(page.Instance, "_draftOperation", new ClayDraftOperation(1, id.ToString("N"), "equipment-save",
            ClayDraftOperationStatus.Confirmed, 1, "{}", [new ClayDraftOperationScope("main", 0, new Dictionary<string, long>())], true, 0));
        SetPrivate(page.Instance, "_draftOperationUnresolved", true);

        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));

        fixture.Service.MainWriteReceipt = new InventoryOperationReceipt(id, InventoryOperationState.Confirmed, 7);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.False(PrivateBool(page.Instance, "_draftOperationUnresolved"));
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }
}
