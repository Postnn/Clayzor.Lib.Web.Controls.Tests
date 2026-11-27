using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Duty;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class DutyTests
{
    [Theory]
    [InlineData("continue")]
    [InlineData("close")]
    [InlineData("reset")]
    public async Task DutyCancelAcceptsThreeChoicesForRawOnlyNewEditor(string action)
    {
        using var db = new Probe();
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true);
        SetupDutyCancelScope(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<DutyEditor>();
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Отмена").HasAttribute("disabled")));
        var day = db.ServerToday.AddDays(1);
        db.ServerToday = day;
        var cancellation = page.InvokeAsync(() => DutyAction(page, "Отмена").ClickAsync());
        const string reset = "Отменить мои изменения и начать заново";
        await SelectDutyExitAsync(dialogs, action == "continue" ? "Продолжить редактирование" :
            action == "close" ? "Закрыть без сохранения" : reset, reset);
        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));

        var replacements = context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        Assert.Equal(action == "close" ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.Equal(action == "close" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        if (action == "reset")
        {
            var replace = Assert.Single(replacements);
            Assert.Equal("main", replace.Arguments[1]);
            Assert.Equal(15L, replace.Arguments[2]);
            using var accepted = JsonDocument.Parse(Assert.IsType<string>(replace.Arguments[3]));
            Assert.Equal(JsonValueKind.Null, accepted.RootElement.GetProperty("Original").ValueKind);
            Assert.Equal(DateOnly.FromDateTime(day), ReadDutyWorkingCopy(page).Date);
        }
        else
            Assert.Empty(replacements);
        AssertNoDutyExitWrite(db, context);
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("closed")]
    [InlineData("missing")]
    [InlineData("read-denied")]
    [InlineData("write-denied")]
    [InlineData("employee")]
    [InlineData("calendar")]
    [InlineData("late")]
    public async Task DutyResetPreservesDraftUntilFreshEditableRowAndRevisionMatch(string mode)
    {
        var started = Original with { DutyDate = Today, ActualId = null, TimeOff = null };
        using var db = new Probe { Row = started };
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true);
        SetupDutyCancelScope(context, mode != "late");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Сохранить").HasAttribute("disabled")));
        await page.InvokeAsync(() => page.FindComponent<Clayzor.Lib.Web.Controls.Components.Select.ClaySelect<DutyEngineer, int?>>()
            .Instance.ValueChanged.InvokeAsync(9));
        Assert.Equal(9, ReadDutyWorkingCopy(page).PlannedId);

        var current = started with { PlannedId = 17, ChangedAt = started.ChangedAt.AddHours(2) };
        db.Row = mode == "missing" ? null : mode == "closed" ? current with { Closed = 1 } : current;
        db.DenyRead = mode == "read-denied";
        db.CanWrite = mode != "write-denied";
        db.CurrentEmployeeId = mode == "employee" ? 5 : 4;
        db.CalendarReadFails = mode == "calendar";
        var cancellation = page.InvokeAsync(() => DutyAction(page, "Отмена").ClickAsync());
        await SelectDutyExitAsync(dialogs, "Отменить мои изменения и загрузить из БД");
        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));

        var replacements = context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        if (mode is "fresh" or "late")
        {
            var replace = Assert.Single(replacements);
            Assert.Equal(15L, replace.Arguments[2]);
            using var accepted = JsonDocument.Parse(Assert.IsType<string>(replace.Arguments[3]));
            Assert.Equal(17, accepted.RootElement.GetProperty("Draft").GetProperty("PlannedId").GetInt32());
            Assert.Equal(current.ChangedAt, accepted.RootElement.GetProperty("Original").GetProperty("ChangedAt").GetDateTime());
            Assert.Equal(17, db.CalendarFor);
        }
        else
            Assert.Empty(replacements);
        Assert.Equal(mode == "fresh" ? 17 : 9, ReadDutyWorkingCopy(page).PlannedId);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        AssertNoDutyExitWrite(db, context);
    }

    [Fact]
    public async Task DutyExitPreservesReadOnlyRecovery()
    {
        using var db = new Probe { Row = Original with { Closed = 1 } };
        var restore = new ClayDraftRestore("duty-readonly-cancel", true, 9, null, null);
        await using var context = EditorContext(db, restore, rawReady: true);
        SetupDutyCancelScope(context);
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261121"));
        page.WaitForAssertion(() => Assert.Contains("Закрыто", page.Markup));
        await page.InvokeAsync(() => DutyAction(page, "Отмена").ClickAsync());

        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        AssertNoDutyExitWrite(db, context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DutyInternalNavigationOnlyProceedsAfterScopedDiscard(bool discard)
    {
        using var db = new Probe { Row = Original with { DutyDate = Today, ActualId = null, TimeOff = null } };
        var navigation = new DutyExitNavigation();
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true, navigation: navigation);
        SetupDutyCancelScope(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Сохранить").HasAttribute("disabled")));
        var moving = page.InvokeAsync(() => navigation.CanNavigateAsync("http://localhost/Duty/DutyForm?id=20261126"));
        dialogs.WaitForAssertion(() => Assert.Contains("Несохранённые изменения", dialogs.Markup));
        Assert.Single(page.FindAll("[data-draft-key='duty.planned']"));
        await SelectDutyExitAsync(dialogs, discard ? "Закрыть без сохранения" : "Продолжить редактирование");

        Assert.Equal(discard, await moving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(discard ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        AssertNoDutyExitWrite(db, context);
    }

    [Fact]
    public async Task DutyResetDoesNotAcceptDelayedReadForPreviousRoute()
    {
        using var db = new Probe { Row = Original with { DutyDate = Today, ActualId = null, TimeOff = null } };
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true);
        SetupDutyCancelScope(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Сохранить").HasAttribute("disabled")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = (DutyAuthentication)context.Services.GetRequiredService<AuthenticationStateProvider>();
        auth.BeforeRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        var cancellation = page.InvokeAsync(() => DutyAction(page, "Отмена").ClickAsync());
        await SelectDutyExitAsync(dialogs, "Отменить мои изменения и загрузить из БД");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.InvokeAsync(() => page.Render(p => p.Add(component => component.RouteId, "20261126")));
        }
        finally { release.TrySetResult(); }
        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        AssertNoDutyExitWrite(db, context);
    }

    [Fact]
    public async Task DutyRamCloseFailureRetainsHandleAndRetryDoesNotSendDml()
    {
        using var db = new Probe();
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true);
        var first = true;
        context.JSInterop.SetupVoid("clayDraftStore.closeRam", call =>
        {
            if (!first || call.Arguments.Count != 1) return false;
            first = false;
            return true;
        }).SetException(new JSException("Тестовый отказ удаления"));
        var page = context.Render<DutyEditor>();
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Сохранить").HasAttribute("disabled")));

        await page.InvokeAsync(() => DutyAction(page, "Отмена").ClickAsync());
        page.WaitForAssertion(() => Assert.Contains("Повторите закрытие", page.Markup));
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);

        await page.InvokeAsync(() => DutyAction(page, "Отмена").ClickAsync());
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        AssertNoDutyExitWrite(db, context);
    }

    [Fact]
    public async Task DutySaveKeepsFormWhenInputChangesAfterAcceptedRevision()
    {
        var start = EditableDutyRow();
        using var db = new Probe { Row = start };
        await using var context = EditorContext(db, EmptyDutyCancelRestore(), rawReady: true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, 1, 1));
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", call => call.Arguments.Count == 2).SetResult(false);
        var page = context.Render<DutyEditor>(p => p.Add(component => component.RouteId, "20261125"));
        page.WaitForAssertion(() => Assert.False(DutyAction(page, "Сохранить").HasAttribute("disabled")));
        await page.InvokeAsync(() => DutyAction(page, "Сохранить").ClickAsync());

        Assert.Equal(1, db.ScheduleWrites);
        Assert.Contains("Более поздний ввод остался", page.Markup);
        Assert.Equal(15L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.closeRam"]).Arguments[1]);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }


    private static ClayDraftRestore EmptyDutyCancelRestore() => new("duty-cancel-fixture", false, 0, null, null);

    private static void SetupDutyCancelScope(BunitContext context, bool accepts = true)
    {
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(15, true));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(accepts);
    }

    private static DutyDraft ReadDutyWorkingCopy(IRenderedComponent<DutyEditor> page) =>
        (DutyDraft)typeof(DutyEditor).GetField("_draft", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page.Instance)!;

    private static AngleSharp.Dom.IElement DutyAction(IRenderedComponent<DutyEditor> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private static Task SelectDutyExitAsync(IRenderedComponent<MudDialogProvider> dialogs, string text, string? label = null)
    {
        dialogs.WaitForAssertion(() => Assert.Contains(text, dialogs.Markup));
        if (label is not null) Assert.Contains(label, dialogs.Markup);
        return dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == text).ClickAsync());
    }

    private static void AssertNoDutyExitWrite(Probe db, BunitContext context)
    {
        Assert.Equal(0, db.ScheduleWrites);
        Assert.Equal(0, db.Commits);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
    }

    private sealed class DutyExitNavigation : NavigationManager
    {
        public DutyExitNavigation() => Initialize("http://localhost/", "http://localhost/Duty/DutyForm?id=20261125");

        protected override void SetNavigationLockState(bool value) { }

        public Task<bool> CanNavigateAsync(string target) =>
            NotifyLocationChangingAsync(target, null, false).AsTask();
    }
}
