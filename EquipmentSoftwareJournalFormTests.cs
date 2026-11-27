using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Сверка установки принимает только исходный ключ и оставляет более поздний ввод.</summary>
    [Fact]
    public async Task SoftwareLostReplyUsesSameDraftAndServerAttemptWithoutResending()
    {
        var fixture = CreateFixture();
        fixture.Service.LoseSoftwareWriteReply = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["clayDraftStore.registerRam"]));
        const int installationId = 15;
        SetActiveChildDraft(page.Instance, nameof(EquipmentSoftwareDialog), "15");
        var value = new EquipmentSoftwareInstallation
        {
            Id = installationId, SoftwareId = 14, InstalledAt = new DateTime(2026, 11, 18),
            Installed = "Версия", Distribution = "Дистрибутив", Notes = "Отправленный ввод",
        };
        fixture.Js.DeferredIdentifier = "clayDraftStore.beginOperationIntent";
        var save = InvokeHandler(page, "SaveSoftwareDraftAsync", value);
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        value.Notes = "Более поздний ввод";
        fixture.Js.Completion.SetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(3));

        var attempt = fixture.Service.SoftwareWriteAttempt;
        Assert.NotEqual(Guid.Empty, attempt);
        Assert.Equal("Отправленный ввод", fixture.Service.SubmittedSoftware?.Notes);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var web = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.Equal(attempt.ToString("N"), web.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal("child:EquipmentSoftwareDialog:15:root", web.RootElement.GetProperty("acceptedScopes")[0].GetString());
        using var data = ReadChildOperationEnvelope(context);
        Assert.Equal(installationId, data.RootElement.GetProperty("Intent").GetProperty("InstallationId").GetInt32());
        Assert.DoesNotContain("Отправленный ввод", data.RootElement.GetRawText());

        fixture.Service.SoftwareWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 16);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");
        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        fixture.Service.SoftwareWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, installationId);
        fixture.Service.StoredSoftware.Add(fixture.Service.SubmittedSoftware!);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Equal(attempt, fixture.Service.SoftwareReadAttempt);
        Assert.Equal(installationId, fixture.Service.SoftwareReadInstallationId);
        Assert.Equal(1, fixture.Service.SoftwareWriteCalls);
        Assert.Equal("Более поздний ввод", value.Notes);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.False(PrivateBool(page.Instance, "_draftOperationUnresolved"));
    }
}
