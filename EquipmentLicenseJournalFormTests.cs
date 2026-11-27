using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Специалист с ролью 44 получает блокировку открытого серверного слота без роли 43.</summary>
    [Fact]
    public async Task LicenseEditorChecksPendingSlotWithoutInventoryEditRole()
    {
        var fixture = CreateFixture();
        fixture.Service.Roles.Clear();
        fixture.Service.Roles.Add(44);
        fixture.Service.LicenseWritePending = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        Assert.False(PrivateBool(page.Instance, "_canEdit"));
        SetPrivate(page.Instance, "_canEditLicense", true);

        await InvokeHandler(page, "OpenLicenseDialogAsync");

        Assert.Equal(1, fixture.Service.LicensePendingReads);
        Assert.Contains("незавершённая операция с оборудованием", page.Markup);
        Assert.Equal(0, fixture.Service.LicenseWriteCalls);
    }

    /// <summary>Получает точное подтверждение SQL после потери ответа с сохранением позднего ввода.</summary>
    [Fact]
    public async Task LicenseLostReplyKeepsTheExactScopeAndOnlyRefreshesAfterProof()
    {
        var fixture = CreateFixture();
        fixture.Service.LoseLicenseWriteReply = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["clayDraftStore.registerRam"]));
        SetPrivate(page.Instance, "_canEditLicense", true);
        SetActiveChildDraft(page.Instance, nameof(EquipmentLicenseDialog), null);
        var draft = new EquipmentLicense { ExpectedExisting = true, VerificationUrl = "https://example.test/license" };

        await InvokeHandler(page, "SaveLicenseDraftAsync", draft);

        var attempt = fixture.Service.LicenseWriteAttempt;
        Assert.NotEqual(Guid.Empty, attempt);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var browser = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.Equal(attempt.ToString("N"), browser.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal("child:EquipmentLicenseDialog:new:root", browser.RootElement.GetProperty("acceptedScopes")[0].GetString());
        using var data = ReadChildOperationEnvelope(context);
        var intent = data.RootElement.GetProperty("Intent");
        Assert.True(intent.GetProperty("ExpectedExisting").GetBoolean());
        Assert.Single(intent.EnumerateObject());

        fixture.Service.LicenseWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 8);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");
        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));
        Assert.Equal(0, fixture.Service.LicenseRefreshCalls);

        FindSn(page).Input("CLAY_TEST_LICENSE_LATER");
        fixture.Service.LicenseWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 7);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Equal(attempt, fixture.Service.LicenseReadAttempt);
        Assert.True(fixture.Service.LicenseReadExpectedExisting);
        Assert.Equal(1, fixture.Service.LicenseWriteCalls);
        Assert.Equal(1, fixture.Service.LicenseRefreshCalls);
        Assert.Equal("CLAY_TEST_LICENSE_LATER", FindSn(page).GetAttribute("value"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }
}
