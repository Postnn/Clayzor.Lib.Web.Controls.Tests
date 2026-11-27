using System.Reflection;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Карточка и допуслуга используют тот же серверный GUID и минимальную привязку браузерной попытки.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimLostReplyReconcilesExactScopeWithoutSavingAgain(bool service)
    {
        var fixture = CreateFixture(sim: true);
        fixture.Service.LoseSimWriteReply = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["clayDraftStore.registerRam"]));
        const string fixturePhone = "70001112223";
        const string fixturePuk = "88112233";
        var dialog = service ? nameof(EquipmentSimServiceDialog) : nameof(EquipmentSimDialog);
        SetActiveChildDraft(page.Instance, dialog, null);

        if (service)
        {
            await InvokeHandler(page, "SaveSimServiceDraftAsync", fixturePhone,
                new EquipmentSimService { Id = 0, ServiceName = "Дополнительная услуга" });
            Assert.NotNull(fixture.Service.SimServiceBusinessDay);
        }
        else
        {
            var opening = typeof(EquipmentForm).GetMethod("CaptureEditorOpening", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page.Instance, null)!;
            var value = new EquipmentSimDialogResult(fixturePhone, DateTime.Today, 19, 20,
                new EquipmentSimSecrets { Puk = fixturePuk }) { Mode = EquipmentSimWriteMode.Add };
            await InvokeHandler(page, "SaveSimDraftAsync", value, opening);
        }

        var attempt = fixture.Service.SimWriteAttempt;
        Assert.NotEqual(Guid.Empty, attempt);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var browser = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.Equal(attempt.ToString("N"), browser.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal($"child:{dialog}:new:root", browser.RootElement.GetProperty("acceptedScopes")[0].GetString());
        using var data = ReadChildOperationEnvelope(context);
        Assert.DoesNotContain(fixturePhone, data.RootElement.GetRawText());
        Assert.DoesNotContain(fixturePuk, data.RootElement.GetRawText());
        Assert.Equal(service ? "ServiceId" : "DuplicateEquipmentId", Assert.Single(data.RootElement.GetProperty("Intent").EnumerateObject()).Name);

        fixture.Service.SimWriteReceipt = new InventoryOperationReceipt(Guid.NewGuid(), InventoryOperationState.Confirmed, 7);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        fixture.Service.SimWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, service ? 91 : 7);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Equal(attempt, fixture.Service.SimReadAttempt);
        Assert.Equal(1, fixture.Service.SimWriteCalls);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.False(PrivateBool(page.Instance, "_draftOperationUnresolved"));
    }
}
