using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class SlBlEditorRestoreTests
{
    [Theory]
    [InlineData("failed", true)]
    [InlineData("pending", false)]
    [InlineData("unrelated", true)]
    public async Task SlBlUsesOnlyOwnReadinessIncludingWhenMailboxWriteIsDenied(string state, bool permitWrite)
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        SetupRawScopeFixture(context);
        SetSlBlReadiness(context, state == "failed" ? new(false, [], ["main"]) :
            state == "pending" ? new(false, ["main"], []) : new(false, [], ["unrelated"]));
        ConfigureEditor(context, sql, permitWrite: permitWrite);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Contains(OldText, form.Markup));
        if (state == "unrelated")
            form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));
        else
        {
            var notice = form.WaitForElement("[data-testid='slbl-input-readiness']");
            form.WaitForAssertion(() => Assert.Contains(state == "failed" ? "Не удалось восстановить" : "ожидает восстановления",
                notice.TextContent));
            Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
            if (!permitWrite) Assert.Contains("Право синхронизации не подтверждено", form.Markup);
        }
        Assert.Equal(0, sql.Dispatches);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("text")]
    [InlineData("while-reading-text")]
    public async Task SlBlRejectsLocalAttemptWhenReadinessOrTextChangesAfterAcknowledgement(string problem)
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        SetupRawScopeFixture(context);
        SetSlBlReadiness(context, new(true, [], []));
        bool Begun() => context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"].Count > 0;
        var lateFailure = false;
        if (problem != "text")
            context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => Begun() && (problem == "pending" || lateFailure))
                .SetResult(new(false, [], ["main"]));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"slbl-late","kind":"slbl.sync","status":"running","revision":9,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var browser = context.JSInterop.SetupModule("./js/slblDraftText.js");
        browser.Setup<string?>("readText", _ => !Begun()).SetResult(OldText);
        browser.Setup<string?>("readText", _ =>
        {
            if (!Begun()) return false;
            lateFailure = problem == "while-reading-text";
            return true;
        }).SetResult(problem == "text" ? LatestText : OldText);
        var dialogs = ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));

        var saving = form.InvokeAsync(() => Button(form, "Сохранить").ClickAsync(new MouseEventArgs()));
        dialogs.WaitForAssertion(() => Assert.Contains("до двух минут", dialogs.Markup));
        await dialogs.InvokeAsync(() => Button(dialogs, "Да").ClickAsync(new MouseEventArgs()));
        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, sql.Dispatches);
        Assert.Equal(9L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]).Arguments[1]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        if (problem == "text") Assert.Contains(LatestText, form.Find("textarea").TextContent);
        else Assert.Contains("Не удалось восстановить", form.Markup);
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("confirmed", false)]
    [InlineData("confirmed", true)]
    public async Task SlBlOwnedOrUnknownOutcomeIsNotOverwrittenByFailedInput(string state, bool serverConfirmed)
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        var intent = new SlBlIntent(Guid.NewGuid(), Address, SlBlKind.Safe, Baseline, LatestText);
        sql.ExternalWrite(LatestText);
        if (serverConfirmed) sql.SeedConfirmed(intent.OperationId);
        var marker = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = intent.OperationId.ToString("N"), kind = "slbl.sync", status = state, revision = 9,
            data = JsonSerializer.Serialize(intent),
            scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        SetupRawScopeFixture(context, marker);
        SetSlBlReadiness(context, new(false, [], ["main"]));
        ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Contains(OldText, form.Markup));
        Assert.DoesNotContain("Повторить восстановление", form.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);

        await form.InvokeAsync(() => Button(form, "Обновить").ClickAsync(new MouseEventArgs()));

        Assert.Equal(serverConfirmed ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.commitOperation"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
        Assert.Equal(0, sql.Dispatches);
        Assert.Contains(OldText, form.Find("textarea").TextContent);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
    }

    private static void SetupRawScopeFixture(BunitContext context, string? marker = null)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-readiness", true, 5, null, marker, new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true).SetResult(new([], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
    }

    private static void SetSlBlReadiness(BunitContext context, ClayDraftRestoreReadiness readiness) =>
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true).SetResult(readiness);
}
