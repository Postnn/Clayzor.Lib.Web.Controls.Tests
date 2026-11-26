using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Maintenance;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class MaintenanceTests
{
    [Theory]
    [InlineData("continue")]
    [InlineData("close")]
    [InlineData("reset")]
    public async Task RecoveredUnsentQuestionSupportsThreeCancelChoices(string choice)
    {
        await using var context = CreateContext(out var db, restoredConfirmation: 17);
        SetConfirmationCancelRevision(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.Contains("Отменить вопрос", page.Markup));
        var readsBefore = db.ListFilters.Count;
        await SelectConfirmationCancelAsync(dialogs, page, choice);

        Assert.Equal(choice == "reset" ? readsBefore + 1 : readsBefore, db.ListFilters.Count);
        Assert.Equal(choice == "continue", page.Markup.Contains("Отменить вопрос", StringComparison.Ordinal));
        var replacements = context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        if (choice == "continue") Assert.Empty(replacements);
        else
        {
            var replace = Assert.Single(replacements);
            Assert.Equal("confirmation", replace.Arguments[1]);
            Assert.Equal(29L, replace.Arguments[2]);
            Assert.Null(replace.Arguments[3]);
        }
        AssertNoCancellationWrite(db, context);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("owner")]
    [InlineData("missing")]
    [InlineData("read-failure")]
    [InlineData("late")]
    public async Task RecoveredQuestionResetRequiresFreshPermissionWorkAndMatchingRevision(string mode)
    {
        await using var context = CreateContext(out var db, restoredConfirmation: 17);
        SetConfirmationCancelRevision(context, mode != "late");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.Contains("Отменить вопрос", page.Markup));
        db.HasRole = mode != "role";
        db.EmployeeId = mode == "owner" ? 8 : 4;
        db.HidePendingWork = mode == "missing";
        db.FailListRead = mode == "read-failure";

        await SelectConfirmationCancelAsync(dialogs, page, "reset");

        Assert.Contains("Отменить вопрос", page.Markup);
        Assert.Equal(mode == "late" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        AssertNoCancellationWrite(db, context);
    }

    [Theory]
    [InlineData("continue", false)]
    [InlineData("close", true)]
    [InlineData("reset", false)]
    public async Task MaintenanceNavigationUsesConfirmationScopeWithoutForcingAReload(string choice, bool leaves)
    {
        var navigation = new MaintenanceGuardNavigation();
        await using var context = CreateContext(out var db, restoredConfirmation: 17, navigation: navigation);
        SetConfirmationCancelRevision(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.Contains("Отменить вопрос", page.Markup));
        var navigating = page.InvokeAsync(() => navigation.CanNavigateAsync("http://localhost/Inventory"));
        await PickConfirmationCancelAsync(dialogs, choice);

        Assert.Equal(leaves, await navigating.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("http://localhost/Maintenance", navigation.Uri);
        Assert.Equal(choice == "continue" ? 0 : 1, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        AssertNoCancellationWrite(db, context);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("confirmed")]
    public async Task SubmittedMaintenanceOutcomeHasNoDestructiveQuestionCancel(string state)
    {
        var operation = $$"""{"version":1,"attemptId":"fixture-maintenance-outcome","kind":"maintenance.complete","status":"{{state}}","revision":3,"data":"17","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";
        await using var context = CreateContext(out var db, restoredOperation: operation, restoredConfirmation: 17);
        var page = context.Render<MaintenancePage>();
        if (state == "unknown")
        {
            page.WaitForAssertion(() => Assert.Contains("Исход выполнения требует проверки", page.Markup));
            AssertNoCancellationWrite(db, context);
        }
        else
        {
            page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]));
            Assert.Equal(0, db.InsertCount);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        }

        Assert.DoesNotContain("Отменить вопрос", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
    }

    [Fact]
    public async Task DiscardedQuestionScopeCanBeOpenedForNextConfirmation()
    {
        await using var context = CreateContext(out var db, restoredConfirmation: 17);
        SetConfirmationCancelRevision(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.Contains("Отменить вопрос", page.Markup));
        await SelectConfirmationCancelAsync(dialogs, page, "close");

        var opening = ClickConfirmation(page);
        dialogs.WaitForAssertion(() => Assert.Contains("Нет", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Нет").ClickAsync());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));

        var open = Assert.Single(context.JSInterop.Invocations["clayDraftStore.openScope"]);
        Assert.Equal("confirmation", open.Arguments[1]);
        AssertNoCancellationWrite(db, context, allowQuestionSave: true);
    }

    private static void SetConfirmationCancelRevision(BunitContext context, bool accepts = true)
    {
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(29, false, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(accepts);
    }

    private static async Task SelectConfirmationCancelAsync(IRenderedComponent<MudDialogProvider> dialogs,
        IRenderedComponent<MaintenancePage> page, string choice)
    {
        var cancel = page.InvokeAsync(() => RecoveryButton(page, "Отменить вопрос").ClickAsync(new MouseEventArgs()));
        await PickConfirmationCancelAsync(dialogs, choice);
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task PickConfirmationCancelAsync(IRenderedComponent<MudDialogProvider> dialogs, string choice)
    {
        var label = choice == "continue" ? "Продолжить редактирование" :
            choice == "close" ? "Закрыть без сохранения" : "Отменить мои изменения и загрузить из БД";
        dialogs.WaitForAssertion(() => Assert.Contains(label, dialogs.Markup));
        return dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == label).ClickAsync());
    }

    private static void AssertNoCancellationWrite(MaintenanceConnection db, BunitContext context, bool allowQuestionSave = false)
    {
        Assert.Equal(0, db.InsertCount);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier is
            "clayDraftStore.beginOperationIntent" or "clayDraftStore.commitOperation" or
            "clayDraftStore.discardScope" or "clayDraftStore.discard");
        Assert.DoesNotContain(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => !allowQuestionSave || !Equals(call.Arguments[1], "confirmation"));
    }

    private sealed class MaintenanceGuardNavigation : NavigationManager
    {
        public MaintenanceGuardNavigation() => Initialize("http://localhost/", "http://localhost/Maintenance");

        protected override void SetNavigationLockState(bool value) { }

        public Task<bool> CanNavigateAsync(string target) =>
            NotifyLocationChangingAsync(target, null, false).AsTask();
    }
}
