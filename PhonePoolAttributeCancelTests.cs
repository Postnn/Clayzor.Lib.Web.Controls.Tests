using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.PhonePoolAttribute;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class PhonePoolAttributeTests
{
    [Theory]
    [InlineData("continue")]
    [InlineData("close")]
    [InlineData("reset")]
    public async Task PoolAttributeUsesSharedThreeWayCancelForRawOnlyInput(string choice)
    {
        using var sql = new Probe { Row = Value("Исходное значение") };
        await using var context = CreateContext(sql);
        SetAttributeScopeRevision(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Исходное значение", page.Markup));
        var changed = sql.Row! with { Value = "Актуальное значение", ChangedAt = sql.Row.ChangedAt.AddHours(2) };
        sql.Row = changed;

        await SelectAttributeCancellationAsync(page, dialogs, choice);

        var replaces = context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        if (choice == "reset")
        {
            var replace = Assert.Single(replaces);
            Assert.Equal("main", replace.Arguments[1]);
            Assert.Equal(13L, replace.Arguments[2]);
            using var snapshot = JsonDocument.Parse(Assert.IsType<string>(replace.Arguments[3]));
            Assert.Equal(changed.Value, snapshot.RootElement.GetProperty("Current").GetProperty("Value").GetString());
            Assert.Equal(changed.ChangedAt, snapshot.RootElement.GetProperty("Version").GetProperty("ChangedAt").GetDateTime());
            Assert.Contains(changed.Value, page.Markup);
        }
        else
        {
            Assert.Empty(replaces);
            if (choice == "continue") Assert.Contains("Исходное значение", page.Markup);
        }
        Assert.Equal(choice == "close" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        Assert.Equal(choice == "close" ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        await page.InvokeAsync(() => AssertNoPoolAttributeCancelWrite(sql, context));
    }

    [Theory]
    [InlineData("employee")]
    [InlineData("role")]
    [InlineData("write-denied")]
    [InlineData("removed")]
    [InlineData("denied")]
    [InlineData("late")]
    public async Task PoolAttributeResetKeepsInputUntilFreshEditableRowAndRevisionMatch(string mode)
    {
        using var sql = new Probe { Row = Value("Сервер") };
        await using var context = CreateContext(sql);
        SetAttributeScopeRevision(context, accepted: mode != "late");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Сервер", page.Markup));
        await page.FindAll("input").Last().InputAsync("Локальное значение");
        sql.EmployeeId = mode == "employee" ? 8 : 4;
        sql.Allowed = mode != "role";
        sql.EditAllowed = mode != "write-denied";
        sql.Row = mode == "removed" ? null : Value("Новая версия");
        sql.DenyRead = mode == "denied";

        await SelectAttributeCancellationAsync(page, dialogs, "reset");

        Assert.Equal("Локальное значение", page.FindAll("input").Last().GetAttribute("value"));
        Assert.Equal(mode == "late" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        await page.InvokeAsync(() => AssertNoPoolAttributeCancelWrite(sql, context));
    }

    [Fact]
    public async Task NewPoolAttributeResetKeepsCreationWithoutWriting()
    {
        using var sql = new Probe();
        await using var context = CreateContext(sql);
        SetAttributeScopeRevision(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.False(Button(page, "Применить").HasAttribute("disabled")));
        await page.FindAll("input").Last().InputAsync("Черновик новой пары");

        await SelectAttributeCancellationAsync(page, dialogs, "reset");

        var replace = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        using var snapshot = JsonDocument.Parse(Assert.IsType<string>(replace.Arguments[3]));
        Assert.Equal(string.Empty, snapshot.RootElement.GetProperty("Current").GetProperty("Value").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("Version").ValueKind);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        await page.InvokeAsync(() => AssertNoPoolAttributeCancelWrite(sql, context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyPoolAttributeExitNeverTouchesPriorRawOrUnknown(bool unknown)
    {
        using var sql = new Probe { TypeExists = false };
        var snapshot = RecoverySnapshot("Ввод в режиме чтения");
        var operation = unknown ? JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "ppa-unknown-exit", kind = "phone-pool-attribute.update", status = "unknown",
            revision = 7, data = snapshot, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        }) : null;
        await using var context = CreateContext(sql, new ClayDraftRestore("ppa-readonly-exit", true, 7, null, operation,
            new Dictionary<string, string> { ["main"] = snapshot }));
        SetAttributeScopeRevision(context);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Тип атрибута не найден", page.Markup));
        Assert.DoesNotContain("Ввод в режиме чтения", page.Markup);
        Assert.DoesNotContain("ppa-unknown-exit", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);

        await page.InvokeAsync(() => Button(page, "Отмена").ClickAsync(new MouseEventArgs()));

        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        await page.InvokeAsync(() => AssertNoPoolAttributeCancelWrite(sql, context));
    }

    [Fact]
    public async Task PoolAttributeRefreshUsesSharedFreshScopeReplacement()
    {
        using var sql = new Probe { Row = Value("Исходное значение") };
        await using var context = CreateContext(sql);
        SetAttributeScopeRevision(context, hasRaw: false);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Исходное значение", page.Markup));
        sql.Row = Value("Версия после перечитывания");

        await page.InvokeAsync(() => Button(page, "Обновить").ClickAsync(new MouseEventArgs()));

        Assert.Contains("Версия после перечитывания", page.Markup);
        var replace = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal("main", replace.Arguments[1]);
        Assert.Equal(13L, replace.Arguments[2]);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        await page.InvokeAsync(() => AssertNoPoolAttributeCancelWrite(sql, context));
    }

    [Fact]
    public async Task PoolAttributeResetIgnoresLateReadOfPreviousCompositeKey()
    {
        using var sql = new Probe { Row = Value("Исходное значение") };
        await using var context = CreateContext(sql);
        SetAttributeScopeRevision(context);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Исходное значение", page.Markup));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sql.BeforeCardRead = () => { entered.TrySetResult(); return release.Task; };

        var cancellation = SelectAttributeCancellationAsync(page, dialogs, "reset");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.InvokeAsync(() => { page.Instance.Id = "invalid"; });
        }
        finally { release.TrySetResult(); }
        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        await page.InvokeAsync(() => AssertNoPoolAttributeCancelWrite(sql, context));
    }

    private static void SetAttributeScopeRevision(BunitContext context, bool accepted = true, bool hasRaw = true)
    {
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(13, hasRaw, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(accepted);
    }

    private static async Task SelectAttributeCancellationAsync(IRenderedComponent<PhonePoolAttributeForm> page,
        IRenderedComponent<MudDialogProvider> dialogs, string choice)
    {
        var cancellation = page.InvokeAsync(() => Button(page, "Отмена").ClickAsync(new MouseEventArgs()));
        var text = choice == "continue" ? "Продолжить редактирование" :
            choice == "close" ? "Закрыть без сохранения" : "Отменить мои изменения и загрузить из БД";
        dialogs.WaitForAssertion(() => Assert.Contains(text, dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == text).ClickAsync());
        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AssertNoPoolAttributeCancelWrite(Probe sql, BunitContext context)
    {
        Assert.Empty(sql.Writes);
        Assert.Equal(0, sql.Commits);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier is
            "clayDraftStore.beginOperationIntent" or "clayDraftStore.commitOperation" or
            "clayDraftStore.discard" or "clayDraftStore.discardScope");
    }
}
