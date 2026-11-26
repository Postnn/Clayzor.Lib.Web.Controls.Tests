using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Duty;
using Microsoft.JSInterop;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class DutyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DutyAutomaticRawRetryDoesNotReapplyRestoredFields(bool fails)
    {
        var row = EditableDutyRow();
        var draft = DutyDraft.FromRow(row) with { PlannedId = 9 };
        using var db = new Probe { Row = row };
        await using var context = EditorContext(db, DutyRawRestore(row, draft), rawReady: true);
        SetDutyRawReadiness(context, new(false, ["main"], []));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new(["data:duty.planned:0"], ["data:duty.time-off:0"]));
        var retry = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true);
        if (fails) retry.SetException(new JSException("fixture failed pending scope"));


        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        page.WaitForAssertion(() => Assert.True(DutyAction(page, "Сохранить").HasAttribute("disabled")));
        await page.InvokeAsync(() => page.FindComponent<ClaySelect<DutyEngineer, int?>>().Instance.ValueChanged.InvokeAsync(11));

        SetDutyRawReadiness(context, new(false, ["unrelated-scope"], []));
        if (!fails) retry.SetResult(new(["data:duty.time-off:0"], []));
        page.WaitForAssertion(() => Assert.Equal(fails, DutyAction(page, "Сохранить").HasAttribute("disabled")));
        Assert.DoesNotContain("Повторить восстановление", page.Markup);

        page.WaitForAssertion(() => Assert.Equal("main", Assert.Single(retry.Invocations).Arguments[1]));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal(11, ReadDutyWorkingCopy(page).PlannedId);
        Assert.Equal(fails, DutyAction(page, "Сохранить").HasAttribute("disabled"));
        AssertNoDutyExitWrite(db, context);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DutyConflictedOrUnknownReopenSurfacesBlockedRawWithoutApplying(bool unknown, bool failed)
    {
        var row = EditableDutyRow();
        var draft = DutyDraft.FromRow(row) with { PlannedId = 9 };
        using var db = new Probe { Row = row with { PlannedId = 17, ChangedAt = row.ChangedAt.AddHours(1) } };
        var snapshot = JsonSerializer.Serialize(new { Original = row, Draft = draft });
        var operation = unknown ? JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "duty-raw-unknown", kind = "duty.save", status = "unknown",
            revision = 7, data = snapshot, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        }) : null;
        await using var context = EditorContext(db, DutyRawRestore(row, draft, operation), rawReady: true);
        SetDutyRawReadiness(context, failed ? new(false, [], ["main"]) : new(false, ["main"], []));
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        var notice = page.WaitForElement("[data-testid='duty-input-readiness']");
        Assert.Contains(failed ? "Не удалось восстановить" : "ожидает восстановления", notice.TextContent);
        Assert.True(DutyAction(page, "Сохранить").HasAttribute("disabled"));
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Trim() == "Повторить восстановление");
        if (unknown) Assert.Contains("duty-raw-unknown", page.Markup);
        await page.InvokeAsync(() => InvokeDutySaveAsync(page.Instance));

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
        Assert.Equal(9, ReadDutyWorkingCopy(page).PlannedId);
        AssertNoDutyExitWrite(db, context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DutySaveRechecksMainRawBeforeDispatch(bool postBegin)
    {
        using var db = new Probe { Row = EditableDutyRow() };
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"duty-raw-gate","kind":"duty.save","status":"running","revision":9,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Сохранить").HasAttribute("disabled")));
        await page.InvokeAsync(() => page.FindComponent<ClaySelect<DutyEngineer, int?>>().Instance.ValueChanged.InvokeAsync(9));

        var pending = new ClayDraftRestoreReadiness(false, ["main"], []);
        if (postBegin)
            context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ =>
                context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"].Count > 0).SetResult(pending);
        else
            SetDutyRawReadiness(context, pending);
        await page.InvokeAsync(() => DutyAction(page, "Сохранить").ClickAsync());

        Assert.NotEmpty(page.FindAll("[data-testid='duty-input-readiness']"));
        Assert.True(DutyAction(page, "Сохранить").HasAttribute("disabled"));
        Assert.Equal(postBegin ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"].Count);
        Assert.Equal(postBegin ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.rejectOperation"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Equal(9, ReadDutyWorkingCopy(page).PlannedId);
        Assert.Equal(0, db.ScheduleWrites);
    }

    [Fact]
    public async Task DutyStalePlanRebaseRechecksRawAfterUserComparison()
    {
        var original = EditableDutyRow();
        using var db = new Probe { Row = original };
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"duty-conflict-raw","kind":"duty.save","status":"running","revision":9,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(12, []));
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Сохранить").HasAttribute("disabled")));
        await page.InvokeAsync(() => page.FindComponent<ClaySelect<DutyEngineer, int?>>().Instance.ValueChanged.InvokeAsync(9));
        db.Row = original with { PlannedId = 17, ChangedAt = original.ChangedAt.AddHours(1) };
        await page.InvokeAsync(() => DutyAction(page, "Сохранить").ClickAsync());
        await page.InvokeAsync(() => DutyAction(page, "Сверить состояние записи").ClickAsync());
        Assert.False(DutyAction(page, "Оставить моего инженера").HasAttribute("disabled"));

        SetDutyRawReadiness(context, new(false, [], ["main"]));
        await page.InvokeAsync(() => DutyAction(page, "Оставить моего инженера").ClickAsync());

        Assert.NotEmpty(page.FindAll("[data-testid='duty-input-readiness']"));
        Assert.Equal(9, ReadDutyWorkingCopy(page).PlannedId);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Equal(0, db.ScheduleWrites);
    }

    private static DutyRow EditableDutyRow() => Original with { DutyDate = Today, ActualId = null, TimeOff = null };

    private static ClayDraftRestore DutyRawRestore(DutyRow row, DutyDraft draft, string? operation = null) =>
        new("duty-raw-fixture", true, 7, null, operation,
            new Dictionary<string, string> { ["main"] = JsonSerializer.Serialize(new { Original = row, Draft = draft }) });

    private static void SetDutyRawReadiness(BunitContext context, ClayDraftRestoreReadiness state) =>
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true).SetResult(state);

    private static Task InvokeDutySaveAsync(DutyEditor editor) =>
        (Task)typeof(DutyEditor).GetMethod("SaveAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, null)!;
}
