using Bunit;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.JSInterop;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private const string PendingApply = "clayDraftStore.applyPendingRaw";
    private const string DraftReadiness = "clayDraftStore.getRestoreReadiness";
    private const string RestoreWarning = "Восстановлена только часть ввода основной формы";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainRawRestore_BlocksBothSavePathsUntilScopedRetry(bool failed)
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("test-registration", true, 19, null, null));
        context.JSInterop.Setup<ClayDraftRawApplyResult>(PendingApply, _ => true)
            .SetResult(new ClayDraftRawApplyResult([], failed ? [] : ["data:equipment.writeoff:0"]));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>(DraftReadiness, _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, failed ? [] : ["main"], failed ? ["main"] : []));
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains(RestoreWarning, page.Markup));
        WaitForSn(page, "CLAY_TEST_7");
        await page.InvokeAsync(() => FindSn(page).Input("CLAY_TEST_NEWER"));

        Assert.True(page.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        await InvokeSave(page, true);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);

        context.JSInterop.Setup<ClayDraftRawApplyResult>(PendingApply, _ => true)
            .SetResult(new ClayDraftRawApplyResult(["data:equipment.writeoff:0"], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>(DraftReadiness, _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, ["child:unrelated:new:root"], []));
        await page.InvokeAsync(() => page.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Обновить").ClickAsync());

        page.WaitForAssertion(() => Assert.DoesNotContain(RestoreWarning, page.Markup));
        WaitForSn(page, "CLAY_TEST_NEWER");
        Assert.Equal(2, context.JSInterop.Invocations[PendingApply].Count);
        Assert.All(context.JSInterop.Invocations[PendingApply], call => Assert.Equal("main", call.Arguments[1]));
        Assert.Single(fixture.Service.Requests);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);

        await InvokeSave(page, false);
        Assert.Equal(1, fixture.Service.UpdateCalls);
    }

    [Fact]
    public async Task MainRawRestore_RevokedRolePreventsReapplyingBrowserValues()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("test-registration", true, 19, null, null));
        context.JSInterop.Setup<ClayDraftRawApplyResult>(PendingApply, _ => true)
            .SetResult(new ClayDraftRawApplyResult([], ["data:equipment.writeoff:0"]));
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains(RestoreWarning, page.Markup));
        fixture.Service.Roles.Remove((int)Роли.Специалист_по_инвентаризации_оборудования);

        await page.InvokeAsync(() => page.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Обновить").ClickAsync());

        Assert.Contains("Не удалось подтвердить доступ к восстановлению ввода", page.Markup);
        Assert.Contains(RestoreWarning, page.Markup);
        Assert.DoesNotContain("Повторить восстановление ввода", page.Markup);
        Assert.Single(context.JSInterop.Invocations[PendingApply]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Fact]
    public async Task ChildRawRestore_ClosingBeforeSuccessfulRetryKeepsTheExactScope()
    {
        const string scope = "child:EquipmentComputerDialog:new:root";
        var fixture = CreateFixture(computer: true);
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", call => Equals(call.Arguments[1], scope))
            .SetResult(new ClayDraftRawApplyResult([], ["data:computer.hd:0"]));
        context.JSInterop.Setup<ClayDraftRawApplyResult>(PendingApply, call => Equals(call.Arguments[1], scope))
            .SetResult(new ClayDraftRawApplyResult([], ["data:computer.hd:0"]));
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        var dialogAdded = TrackNextDialog(context);
        var open = InvokeHandler(page, "OpenComputerDialogAsync");
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        Assert.Equal(scope, context.JSInterop.Invocations["clayDraftStore.applyRaw"][0].Arguments[1]);

        await provider.InvokeAsync(() => dialog.Close(DialogResult.Cancel()));
        await open.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(context.JSInterop.Invocations["clayDraftStore.discardScope"],
            call => Equals(call.Arguments[1], scope));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.All(context.JSInterop.Invocations[PendingApply], call => Assert.Equal(scope, call.Arguments[1]));
        Assert.NotNull(GetPrivate(page.Instance, "_activeEditor"));
        Assert.Equal(0, fixture.Service.ComputerSaveCalls);
    }
}
