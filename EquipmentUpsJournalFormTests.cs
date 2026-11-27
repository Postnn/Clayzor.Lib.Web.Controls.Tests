using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Связь и пакет сверяются по исходному GUID и ИБП без повторной записи.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpsLostReplyReconcilesExactPairAndPreservesLaterInput(bool batch)
    {
        var fixture = CreateFixture();
        fixture.Service.UpsSaveException = new EquipmentOperationException(EquipmentWritePhase.Unknown,
            "Ответ потерян.", new TimeoutException());
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["clayDraftStore.registerRam"]));
        SetPrivate(page.Instance, "_canManageUps", true);
        const int upsId = 10;
        if (batch)
        {
            SetActiveChildDraft(page.Instance, nameof(EquipmentUpsDialog), "10");
            var link = new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 7 };
            await InvokeHandler(page, "SaveUpsDraftAsync", link, new[] { link }, Array.Empty<UpsTest>(),
                new EquipmentUpsDialogResult([link], []));
        }
        else
            await InvokeHandler(page, "AddUpsLinkAsync", upsId);

        var attempt = fixture.Service.UpsWriteAttempt;
        Assert.NotEqual(Guid.Empty, attempt);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var browser = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.Equal(attempt.ToString("N"), browser.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(batch ? 1 : 0, browser.RootElement.GetProperty("acceptedScopes").GetArrayLength());
        using var data = ReadChildOperationEnvelope(context);
        var intent = data.RootElement.GetProperty("Intent");
        Assert.Equal(upsId, intent.GetProperty("UpsEquipmentId").GetInt32());
        Assert.Single(intent.EnumerateObject());

        fixture.Service.UpsWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 8);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, fixture.Service.UpsRefreshCalls);

        FindSn(page).Input("CLAY_TEST_UPS_LATER");
        fixture.Service.UpsWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 7);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Equal(attempt, fixture.Service.UpsReadAttempt);
        Assert.Equal(upsId, fixture.Service.UpsReadId);
        Assert.Equal(1, fixture.Service.UpsWriteCalls);
        Assert.Equal(1, fixture.Service.UpsRefreshCalls);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal("CLAY_TEST_UPS_LATER", FindSn(page).GetAttribute("value"));
    }
}
