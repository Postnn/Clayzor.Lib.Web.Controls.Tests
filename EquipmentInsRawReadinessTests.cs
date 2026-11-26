using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentInsTests
{
    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "failed")]
    [InlineData(true, "unrelated")]
    public async Task ReopenedCreationUsesMainReadinessForNewAndCopy(bool copy, string state)
    {
        var uri = "http://localhost/Equipment/EquipmentIns" + (copy ? "?copyId=7" : "");
        var payload = await CaptureEditorDraftAsync(uri);
        var fixture = CreateFixture(uri, restoredCreation: EditorRestore(payload));
        await using var context = fixture.Context;
        SetCreationReadiness(context, state == "pending" ? new(false, ["main"], []) :
            state == "failed" ? new(false, [], ["main"]) : new(false, [], ["creation-result"]));
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Contains("Локальная правка", page.Markup));
        page.WaitForAssertion(() =>
        {
            if (state == "unrelated")
                Assert.False(FindButton(page, "Применить").HasAttribute("disabled"));
            else
                Assert.Contains(state == "failed" ? "Не удалось восстановить" : "ожидает восстановления",
                    page.Find("[data-testid='ins-input-readiness']").TextContent);
        });
        if (state != "unrelated")
        {
            Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
            await InvokeSave(page, false);
        }
        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("failure")]
    [InlineData("pending")]
    public async Task CreationRawRetryUsesOnlyPendingFieldsAndKeepsLiveText(string outcome)
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var fixture = CreateFixture(uri, restoredCreation: EditorRestore(await CaptureEditorDraftAsync(uri)), pendingEditorRaw: true);
        await using var context = fixture.Context;
        var retry = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true);
        if (outcome == "failure") retry.SetException(new JSException("fixture failed raw"));
        else retry.SetResult(outcome == "pending" ? new([], ["data:ins.date:0"]) : new(["data:ins.date:0"], []));
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("[data-testid='ins-input-readiness']");
        await page.Find("textarea[data-draft-key='ins.notes']").InputAsync("Поздний текст");
        SetCreationReadiness(context, new(true, [], []));

        await page.InvokeAsync(() => FindButton(page, "Обновить").ClickAsync());

        Assert.Equal("main", Assert.Single(retry.Invocations).Arguments[1]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal("Поздний текст", CurrentModel(page.Instance).Description);
        Assert.Equal(outcome != "ready", FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationRechecksMainBeforeDispatchAfterLocalAcknowledgement(bool postBegin)
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Применить").HasAttribute("disabled")));
        var failed = new ClayDraftRestoreReadiness(false, [], ["main"]);
        if (postBegin)
            context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ =>
                context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"].Count > 0).SetResult(failed);
        else
            SetCreationReadiness(context, failed);

        await page.InvokeAsync(() => FindButton(page, "Применить").ClickAsync());

        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Equal(postBegin ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"].Count);
        Assert.Equal(postBegin ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.rejectOperation"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Contains("Не удалось восстановить", page.Find("[data-testid='ins-input-readiness']").TextContent);
    }

    [Fact]
    public async Task UnavailableCopyCanRetryRawWithoutDiscardingItsSourceRecoveryPath()
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var fixture = CreateFixture(uri, restoredCreation: EditorRestore(await CaptureEditorDraftAsync(uri)), pendingEditorRaw: true);
        fixture.Service.CopyAvailable = false;
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
            .SetResult(new(["data:ins.notes:0"], []));
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("[data-testid='ins-input-readiness']");
        SetCreationReadiness(context, new(true, [], []));

        await page.InvokeAsync(() => FindButton(page, "Повторить восстановление ввода").ClickAsync());

        Assert.Single(fixture.Service.CopyRequests);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
        Assert.Contains("Локальная правка", page.Markup);
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    [Fact]
    public async Task CreationKeepsLateTypedInputAfterAcknowledgement()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Применить").HasAttribute("disabled")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readiness = context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ =>
        {
            if (context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"].Count == 0)
                return false;
            entered.TrySetResult();
            return true;
        });

        var saving = page.InvokeAsync(() => FindButton(page, "Применить").ClickAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.InvokeAsync(() => { CurrentModel(page.Instance).Description = "Поздний текст"; });
        }
        finally { readiness.SetResult(new(true, [], [])); }
        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Поздний текст", CurrentModel(page.Instance).Description);
        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }

    private static void SetCreationReadiness(BunitContext context, ClayDraftRestoreReadiness state) =>
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true).SetResult(state);
}
