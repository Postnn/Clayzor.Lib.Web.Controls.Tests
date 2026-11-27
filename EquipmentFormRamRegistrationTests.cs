using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private const string BeginEquipmentTransfer = "clayDraftStore.beginLegacyOperationEvidenceTransfer";
    private const string RegisterEquipmentRam = "clayDraftStore.registerRam";
    private const string RetireEquipmentLegacy = "clayDraftStore.retireLegacyOperationEvidenceTransfer";

    [Fact]
    public async Task RamRegistration_ImportsOwnOperationBeforeAttachAndContinuesToBlockPendingSave()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var evidence = new ClayLegacyDraftOperationEvidence(Guid.NewGuid().ToString("N"), "equipment-save", "{}");
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]>(BeginEquipmentTransfer, _ => true).SetResult([evidence]);
        fixture.Service.LegacyEquipmentPending = true;
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations[RegisterEquipmentRam]));

        Assert.Equal(evidence, Assert.Single(fixture.Service.ImportedEquipmentEvidence));
        var transfer = Assert.Single(context.JSInterop.Invocations[BeginEquipmentTransfer]);
        var registration = Assert.Single(context.JSInterop.Invocations[RegisterEquipmentRam]);
        Assert.Equal("equipment-form", transfer.Arguments[0]);
        Assert.Equal("7", transfer.Arguments[1]);
        Assert.Equal("inventory:" + registration.Arguments[2], transfer.Arguments[2]);
        Assert.Equal("equipment-form", registration.Arguments[0]);
        Assert.True(Guid.TryParse(Assert.IsType<string>(registration.Arguments[5]), out var instance));
        Assert.NotEqual(Guid.Empty, instance);
        var calls = context.JSInterop.Invocations.Select(call => call.Identifier).ToList();
        Assert.True(calls.IndexOf(BeginEquipmentTransfer) < calls.IndexOf(RegisterEquipmentRam));
        Assert.True(calls.IndexOf(RegisterEquipmentRam) < calls.IndexOf(RetireEquipmentLegacy));
        Assert.True(calls.IndexOf(RetireEquipmentLegacy) < calls.IndexOf("clayDraftStore.finishLegacyOperationEvidenceTransfer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeHandler(page, "BeginDraftOperationAsync", "equipment-save", null));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTyped"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Fact]
    public async Task RamRegistration_FailedImportBlocksAttachAndRefreshRetriesWithoutWriting()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        fixture.Service.LegacyEquipmentImportError = new UnauthorizedAccessException();
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Не удалось открыть защищённый черновик", page.Markup));
        Assert.Empty(context.JSInterop.Invocations[RegisterEquipmentRam]);
        Assert.Empty(context.JSInterop.Invocations[RetireEquipmentLegacy]);

        fixture.Service.LegacyEquipmentImportError = null;
        await InvokeHandler(page, "RefreshAsync");

        Assert.Single(context.JSInterop.Invocations[RegisterEquipmentRam]);
        Assert.Single(context.JSInterop.Invocations[RetireEquipmentLegacy]);
        Assert.Equal(2, context.JSInterop.Invocations[BeginEquipmentTransfer].Count);
        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"].Count);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.DoesNotContain("Не удалось открыть защищённый черновик", page.Markup);
    }

    [Fact]
    public async Task RamRegistration_KeyFailureDoesNotDeleteLegacySnapshots()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestore?>(RegisterEquipmentRam, _ => true)
            .SetException(new Microsoft.JSInterop.JSException("key refused"));
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Не удалось открыть защищённый черновик", page.Markup));

        Assert.Single(context.JSInterop.Invocations[RegisterEquipmentRam]);
        Assert.Empty(context.JSInterop.Invocations[RetireEquipmentLegacy]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Fact]
    public async Task RamRegistration_DoesNotListOrAdoptAClosedEditor()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations[RegisterEquipmentRam]));

        await InvokeHandler(page, "RefreshAsync");

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.listRecoveryCandidates"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recoverListedDraft"]);
        Assert.DoesNotContain("Сохранённый ввод", page.Markup);
        Assert.DoesNotContain("Открыть черновик", page.Markup);
        Assert.DoesNotContain("Проверить сохранённые черновики", page.Markup);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }
}
