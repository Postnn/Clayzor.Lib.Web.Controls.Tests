using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Восстановление и принятие сырого ввода через реальный редактор SlBl.</summary>
public sealed partial class SlBlEditorRestoreTests
{
    /// <summary>Почтовая отправка регистрируется до вызова, а потерянный ответ блокирует новый идентификатор.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MailboxDispatchUsesDurableAttemptWithoutResending(bool loseReply)
    {
        DapperColumnMapper.Initialize();
        using var connection = new MailboxConnection { LoseDispatchReply = loseReply };
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var adapter = new SlBlSqlSynchronization(db, "fixture", new FixtureWritePolicy(true));
        var intent = new SlBlIntent(Guid.NewGuid(), Address, SlBlKind.Safe, Baseline, LatestText);
        if (loseReply)
            await Assert.ThrowsAsync<TimeoutException>(() => adapter.DispatchAsync(intent, default));
        else
            await adapter.DispatchAsync(intent, default);

        var state = await adapter.ReadWriteStateAsync(Address, SlBlKind.Safe, default);
        Assert.True(state.Allowed);
        Assert.Equal(loseReply, state.Pending);
        if (loseReply)
            await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
                adapter.DispatchAsync(intent with { OperationId = Guid.NewGuid(), Original = LatestText }, default));
        else
            await adapter.DispatchAsync(intent, default);

        Assert.Equal(1, connection.Dispatches);
    }
    private const string Address = "mailbox@example.invalid";
    private const string Baseline = "base.example.invalid";
    private const string OldText = "older.example.invalid";
    private const string LatestText = "newer.example.invalid";

    /// <summary>Неоконченное применение raw блокирует Begin. Повтор использует точный последний текст.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SaveWaitsForRawAndUsesBrowserValue(bool throws, bool changesDuringConfirmation)
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var ready = false;
        var browserChanged = false;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-fixture", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        var initial = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => !ready);
        if (throws) initial.SetException(new JSException("Временная ошибка применения"));
        else initial.SetResult(new ClayDraftRawApplyResult([], ["data:slbl.text"]));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => ready)
            .SetResult(new ClayDraftRawApplyResult(["data:slbl.text"], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => !ready)
            .SetResult(new ClayDraftRestoreReadiness(false, ["main"], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => ready)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"slbl-test","kind":"slbl.sync","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var browser = context.JSInterop.SetupModule("./js/slblDraftText.js");
        browser.Setup<string?>("readText", _ => !browserChanged).SetResult(LatestText);
        browser.Setup<string?>("readText", _ => browserChanged).SetResult("changed.example.invalid");

        var dialogs = ConfigureEditor(context, sql);

        var form = context.Render<SlBlForm>(parameters => parameters.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        var save = form.FindComponents<ClayToolbarButton>().Single(button => button.Instance.Options.Text == "Сохранить");
        await form.InvokeAsync(() => save.Instance.OnClick.InvokeAsync());
        Assert.Equal(0, sql.Dispatches);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);

        ready = true;
        await form.InvokeAsync(() => Button(form, "Обновить").ClickAsync(new MouseEventArgs()));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));
        var saving = Button(form, "Сохранить").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("до двух минут", dialogs.Markup));
        browserChanged = changesDuringConfirmation;
        await Button(dialogs, "Да").ClickAsync(new MouseEventArgs());
        if (changesDuringConfirmation)
        {
            await saving;
            Assert.Contains("Текст списка изменился", form.Markup);
            var saved = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last();
            using var rawDraft = JsonDocument.Parse((string)saved.Arguments[2]!);
            Assert.Equal("changed.example.invalid", rawDraft.RootElement.GetProperty("Current").GetString());
            Assert.Equal(0, sql.Dispatches);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
            return;
        }
        dialogs.WaitForAssertion(() => Assert.Contains("Закрыть форму", dialogs.Markup));
        await Button(dialogs, "Нет").ClickAsync(new MouseEventArgs());
        await saving;

        Assert.Equal(1, sql.Dispatches);
        Assert.Equal(LatestText, sql.CurrentText);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var wire = JsonDocument.Parse((string)begin.Arguments[1]!);
        using var intent = JsonDocument.Parse(wire.RootElement.GetProperty("data").GetString()!);
        Assert.Equal(LatestText, intent.RootElement.GetProperty("Desired").GetString());
        Assert.Equal("main", Assert.Single(wire.RootElement.GetProperty("acceptedScopes").EnumerateArray()).GetString());
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Известный результат проходит cleanup и перечитывание без повторной отправки и сверки исхода.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedSaveRetriesOnlyTheUnfinishedPhase(bool failCleanup)
    {
        using var sql = new MailboxConnection { FailReadAfterDispatch = !failCleanup };
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-verified", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"slbl-verified-attempt","kind":"slbl.sync","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.JSInterop.SetupModule("./js/slblDraftText.js").Setup<string?>("readText", _ => true).SetResult(LatestText);
        var failNextCommit = failCleanup;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!failNextCommit) return false;
            failNextCommit = false;
            return true;
        }).SetException(new JSException("Тестовый отказ browser cleanup"));

        var dialogs = ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));
        var saving = Button(form, "Сохранить").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("до двух минут", dialogs.Markup));
        await Button(dialogs, "Да").ClickAsync(new MouseEventArgs());
        await saving;

        Assert.Contains("Синхронизация подтверждена", form.Markup);
        Assert.Contains(failCleanup ? "обработку черновика" : "список не перечитан", form.Markup);
        Assert.Equal(1, sql.Dispatches);
        Assert.Equal(1, sql.ComparisonReads);
        Assert.Contains(LatestText, form.Markup);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));

        sql.FailReadAfterDispatch = false;
        sql.ExternalWrite("external.example.invalid");
        await Button(form, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("external.example.invalid", form.Markup);
        Assert.Equal(1, sql.Dispatches);
        Assert.Equal(1, sql.ComparisonReads);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(6L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]).Arguments[1]);
        var order = context.JSInterop.Invocations.Select(call => call.Identifier).ToList();
        Assert.True(order.IndexOf("clayDraftStore.recordConfirmedOperation") < order.IndexOf("clayDraftStore.commitOperation"));
        var commits = context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        Assert.Equal(failCleanup ? 2 : 1, commits.Count);
        Assert.All(commits, call => Assert.Equal(6L, call.Arguments[1]));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }

    /// <summary>Новый документ без почтовой политики не открывает прежний ключ или сырой ввод.</summary>
    [Fact]
    public async Task NoWritePolicyDoesNotOpenLegacyEditor()
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("legacy-private", true, 7, null, null,
                new Dictionary<string, string> { ["main"] = JsonSerializer.Serialize(OldText) }));
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-ram", false, 0, null, null));
        ConfigureEditor(context, sql, permitWrite: false);

        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Contains(Baseline, form.Markup));

        Assert.DoesNotContain(OldText, form.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(form.FindComponents<ClayPresencePanel>());
    }

    /// <summary>Присутствие проверяет сотрудника и точный список при каждом опросе.</summary>
    [Fact]
    public async Task PresenceRequiresCurrentActorAndExactMailboxPolicy()
    {
        DapperColumnMapper.Initialize();
        using var sql = new MailboxConnection();
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => sql);
        var principal = (await new FixtureAuthentication().GetAuthenticationStateAsync()).User;
        var allowed = new FixtureWritePolicy(true);

        var access = await SlBlForm.AuthorizePresenceAsync(db, allowed, principal, 4, Address, SlBlKind.Safe, default);
        Assert.NotNull(access);
        Assert.Equal(4, access.EmployeeId);
        Assert.Equal(ClayPresenceActivity.Editing, access.Activity);
        Assert.Null(await SlBlForm.AuthorizePresenceAsync(db, new FixtureWritePolicy(false), principal, 4, Address, SlBlKind.Safe, default));
        Assert.Null(await SlBlForm.AuthorizePresenceAsync(db, allowed, principal, 5, Address, SlBlKind.Safe, default));
        Assert.Null(await SlBlForm.AuthorizePresenceAsync(db, allowed, principal, 4, Address, SlBlKind.Blocked, default));
        Assert.Null(await SlBlForm.AuthorizePresenceAsync(db, allowed, new ClaimsPrincipal(), 4, Address, SlBlKind.Safe, default));
        Assert.Equal(0, sql.Dispatches);
    }

    /// <summary>Прежняя попытка переносится как блокировка до RAM-регистрации без отправки почтовой команды.</summary>
    [Fact]
    public async Task LegacyEvidenceBlocksRamSaveWithoutRestoringPriorInput()
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var id = Guid.NewGuid();
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-ram", false, 0, null, null));
        ConfigureEditor(context, sql);
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([new(id.ToString("N"), "slbl.sync", Address + ":0")]);

        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Contains("В серверном журнале есть незавершённая синхронизация", form.Markup));

        Assert.True(sql.HasLegacyEvidence(id));
        Assert.Equal(0, sql.Dispatches);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        var calls = context.JSInterop.Invocations.Select(call => call.Identifier).ToList();
        Assert.True(calls.IndexOf("clayDraftStore.registerRam") <
            calls.IndexOf("clayDraftStore.retireLegacyOperationEvidenceTransfer"));
        Assert.True(calls.IndexOf("clayDraftStore.retireLegacyOperationEvidenceTransfer") <
            calls.IndexOf("clayDraftStore.finishLegacyOperationEvidenceTransfer"));
    }

    /// <summary>Повтор после сбоя удаления перед закрытием использует один RAM-дескриптор и одну ревизию.</summary>
    [Fact]
    public async Task CloseFailureRetriesSamePhysicalCleanupWithoutMailDispatch()
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-ram", false, 0, null, null));
        ConfigureEditor(context, sql);
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true)
            .SetException(new JSException("fixture cleanup failed"));
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.False(Button(form, "Отмена").HasAttribute("disabled")));

        await Button(form, "Отмена").ClickAsync(new MouseEventArgs());

        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Contains("Удаление черновика не подтверждено", form.Markup);
        var first = Assert.Single(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(true);
        await Button(form, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        var last = context.JSInterop.Invocations["clayDraftStore.closeRam"].Last();
        Assert.Equal(first.Arguments[0], last.Arguments[0]);
        Assert.Equal(first.Arguments[1], last.Arguments[1]);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(0, sql.Dispatches);
    }

    [Theory]
    [InlineData("Продолжить редактирование", false, false, true)]
    [InlineData("Закрыть без сохранения", true, false, true)]
    [InlineData("Отменить мои изменения и загрузить из БД", false, true, true)]
    [InlineData("Отменить мои изменения и загрузить из БД", false, true, false)]
    public async Task CancelSelectsContinueCloseOrResetWithExactRevision(string action, bool closes, bool resets, bool matches)
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-edit", true, 8, null, null,
                new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        var dialogs = ConfigureEditor(context, sql);
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(matches);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Contains(OldText, form.Find("textarea").TextContent));
        sql.ExternalWrite(LatestText);

        var cancel = form.InvokeAsync(() => Button(form, "Отмена").ClickAsync(new MouseEventArgs()));
        dialogs.WaitForAssertion(() => Assert.NotNull(Button(dialogs, action)));
        await dialogs.InvokeAsync(() => Button(dialogs, action).ClickAsync(new MouseEventArgs()));
        await cancel;

        Assert.Equal(closes ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        Assert.Equal(closes ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.Equal(resets ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        if (resets)
        {
            var replacement = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal("main", replacement.Arguments[1]);
            Assert.Equal(8L, replacement.Arguments[2]);
            Assert.Contains(LatestText, Assert.IsType<string>(replacement.Arguments[3]));
        }
        if (!closes) Assert.Contains(resets && matches ? LatestText : OldText, form.Find("textarea").TextContent);
        Assert.Equal(0, sql.Dispatches);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingUnknownDraftRequiresServerEvidenceBeforePhysicalCleanup(bool authorized)
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var id = Guid.NewGuid();
        var intent = new SlBlIntent(id, Address, SlBlKind.Safe, Baseline, LatestText);
        var marker = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = id.ToString("N"), kind = "slbl.sync", status = "unknown", revision = 6,
            data = JsonSerializer.Serialize(intent), scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-unknown", true, 8, null, marker));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        var dialogs = ConfigureEditor(context, sql, permitWrite: authorized);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(8, false, true));
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence>("clayDraftStore.readCloseOperationEvidence", _ => true)
            .SetResult(new ClayLegacyDraftOperationEvidence(id.ToString("N"), "slbl.sync", Address + ":0"));
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.False(Button(form, "Отмена").HasAttribute("disabled")));

        var cancel = form.InvokeAsync(() => Button(form, "Отмена").ClickAsync(new MouseEventArgs()));
        dialogs.WaitForAssertion(() => Assert.NotNull(Button(dialogs, "Закрыть без сохранения")));
        await dialogs.InvokeAsync(() => Button(dialogs, "Закрыть без сохранения").ClickAsync(new MouseEventArgs()));
        await cancel;

        Assert.Equal(authorized, sql.HasLegacyEvidence(id));
        Assert.Equal(authorized ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        Assert.Equal(authorized ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.Equal(0, sql.Dispatches);
    }

    /// <summary>Повторное открытие известного результата сохраняет новый черновик; совпадение Unknown не подтверждает исход.</summary>
    [Theory]
    [InlineData("confirmed", true, false)]
    [InlineData("confirmed", false, true)]
    [InlineData("unknown", true, false)]
    public async Task RestoredReceiptOnlyCleansConfirmedAndPreservesLaterInput(string status, bool hasTyped, bool hasRaw)
    {
        using var sql = new MailboxConnection();
        sql.ExternalWrite(LatestText);
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        var intent = new SlBlIntent(Guid.NewGuid(), Address, SlBlKind.Safe, Baseline, LatestText);
        if (status == "confirmed")
            sql.SeedConfirmed(intent.OperationId);
        var command = JsonSerializer.Serialize(intent);
        var marker = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = intent.OperationId.ToString("N"), kind = "slbl.sync", status, revision = 6, data = command,
            scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-previous", true, 8, null, marker,
                hasTyped ? new Dictionary<string, string> { ["main"] = snapshot } : null));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));

        ConfigureEditor(context, sql, hasRaw);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        var expected = hasTyped ? OldText : LatestText;
        form.WaitForAssertion(() => Assert.Contains(expected, form.Markup));
        if (status == "confirmed")
            sql.ExternalWrite("external.example.invalid");
        await Button(form, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains(expected, form.Find("textarea").TextContent);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        if (status == "confirmed")
        {
            Assert.Contains("Синхронизация подтверждена", form.Markup);
            Assert.Equal(6L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
            Assert.Equal(0, sql.ComparisonReads);
            if (hasRaw)
                form.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
            else
                Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        }
        else
        {
            Assert.Contains("исход попытки не подтверждён", form.Markup);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
            Assert.Equal(1, sql.ComparisonReads);
        }
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Equal(0, sql.Dispatches);
    }

    /// <summary>Частичный результат сохраняет фактический baseline вместе с локальным текстом и intent.</summary>
    [Fact]
    public async Task PartialResultPersistsBothCanonicalBaselineAndLocalIntent()
    {
        const string canonical = "partial.example.invalid";
        using var sql = new MailboxConnection { PartialText = canonical };
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-partial", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"slbl-partial-attempt","kind":"slbl.sync","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.JSInterop.SetupModule("./js/slblDraftText.js").Setup<string?>("readText", _ => true).SetResult(LatestText);

        var dialogs = ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));
        var saving = Button(form, "Сохранить").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("до двух минут", dialogs.Markup));
        await Button(dialogs, "Да").ClickAsync(new MouseEventArgs());
        await saving;

        Assert.Contains("Список сохранён частично", form.Markup);
        Assert.Contains(canonical, form.Markup);
        Assert.Contains(LatestText, form.Markup);
        var call = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last();
        using var draft = JsonDocument.Parse((string)call.Arguments[2]!);
        Assert.Equal(canonical, draft.RootElement.GetProperty("Original").GetString());
        Assert.Equal(LatestText, draft.RootElement.GetProperty("Current").GetString());
        Assert.Equal(LatestText, draft.RootElement.GetProperty("PartialIntent").GetProperty("Desired").GetString());
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Equal(1, sql.Dispatches);
    }

    /// <summary>После расхождения baseline пользователь сам выбирает значение с защитой ревизии.</summary>
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ConflictRebaseUsesExplicitChoiceAndExpectedRevision(bool keepLocal, bool revisionMatches, bool invalidRaw)
    {
        const string fresh = "fresh.example.invalid";
        var local = invalidRaw ? "<неполный>" : LatestText;
        using var sql = new MailboxConnection();
        sql.ExternalWrite(fresh);
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("slbl-conflict", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(7, ["data:slbl.text", "data:unrelated"]));
        context.JSInterop.Setup<bool>("clayDraftStore.rebaseScope", _ => true).SetResult(revisionMatches);
        context.JSInterop.SetupModule("./js/slblDraftText.js").Setup<string?>("readText", _ => true).SetResult(local);

        ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Contains("Список в базе изменился", form.Markup));

        await Button(form, "Обновить").ClickAsync(new MouseEventArgs()).WaitAsync(TimeSpan.FromSeconds(2));
        var comparison = form.Find("[aria-label='Сравнение списков']");
        Assert.Contains(Baseline, comparison.TextContent);
        Assert.Contains(local, comparison.TextContent);
        Assert.Contains(fresh, comparison.TextContent);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);

        await Button(form, keepLocal ? "Оставить мой список" : "Принять список из базы").ClickAsync(new MouseEventArgs());

        var request = Assert.Single(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        Assert.Equal("main", request.Arguments[1]);
        Assert.Equal(7L, request.Arguments[2]);
        using var rebased = JsonDocument.Parse((string)request.Arguments[3]!);
        Assert.Equal(fresh, rebased.RootElement.GetProperty("Original").GetString());
        Assert.Equal(keepLocal ? local : fresh, rebased.RootElement.GetProperty("Current").GetString());
        Assert.Equal(keepLocal ? Array.Empty<string>() : ["data:slbl.text"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(request.Arguments[4]));
        Assert.Equal(0, sql.Dispatches);
        Assert.Equal(invalidRaw ? 0 : 1, sql.ComparisonReads);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);

        if (revisionMatches)
        {
            Assert.Empty(form.FindAll("[aria-label='Сравнение списков']"));
            Assert.Equal(!keepLocal, Button(form, "Сохранить").HasAttribute("disabled"));
            Assert.Contains(keepLocal ? local : fresh, form.Find("textarea").TextContent);
        }
        else
        {
            Assert.Contains("Черновик изменился", form.Markup);
            Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        }
    }

    private static IRenderedComponent<MudDialogProvider> ConfigureEditor(BunitContext context, MailboxConnection sql, bool remainingRaw = false, bool permitWrite = true)
    {
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(8, remainingRaw, false));
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([]);
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(true);
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        DapperColumnMapper.Initialize();
        context.Services.AddScoped(_ => new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => sql));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
        context.Services.AddSingleton<ISlBlMailboxWritePolicy>(new FixtureWritePolicy(permitWrite));
        context.Services.AddMudServices();
        var dialogs = context.Render<MudDialogProvider>();
        context.Render<MudPopoverProvider>();
        return dialogs;
    }

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> component, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        component.FindAll("button").Single(button => button.TextContent.Trim() == name);

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class FixtureWritePolicy(bool permitted) : ISlBlMailboxWritePolicy
    {
        public Task<bool> AllowsAsync(Employee actor, string address, SlBlKind kind, CancellationToken ct) =>
            Task.FromResult(permitted && actor.Id == 4 && address == Address && kind == SlBlKind.Safe);
    }

    private sealed class MailboxConnection : DbConnection
    {
        internal int Dispatches { get; private set; }
        internal string CurrentText { get; private set; } = Baseline;
        internal int ComparisonReads { get; private set; }
        internal bool FailReadAfterDispatch { get; set; }
        internal bool LoseDispatchReply { get; init; }
        internal string? PartialText { get; init; }
        private readonly Dictionary<Guid, JournalEntry> _journal = [];
        private readonly Dictionary<Guid, JournalEntry> _legacy = [];
        internal bool HasLegacyEvidence(Guid id) => _legacy.ContainsKey(id);
        internal void ExternalWrite(string text) => CurrentText = text;
        internal void SeedConfirmed(Guid id)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Address.ToUpperInvariant())));
            _journal[id] = new JournalEntry(id, 4, $"slbl/{hash}/{(byte)SlBlKind.Safe}", InventoryOperationState.Confirmed);
        }
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => new JournalTransaction(this, level);
        protected override DbCommand CreateDbCommand() => new Command(this);

        private sealed record JournalEntry(Guid AttemptId, int EmployeeId, string Scope, InventoryOperationState State);

        private sealed class JournalTransaction(MailboxConnection owner, IsolationLevel isolation) : DbTransaction
        {
            internal JournalEntry? Pending { get; set; }
            internal JournalEntry? Legacy { get; set; }
            public override IsolationLevel IsolationLevel => isolation;
            protected override DbConnection DbConnection => owner;
            public override void Commit()
            {
                if (Pending is { } entry) owner._journal[entry.AttemptId] = entry;
                if (Legacy is { } legacy) owner._legacy[legacy.AttemptId] = legacy;
            }
            public override void Rollback() => Pending = null;
        }

        private sealed class Command(MailboxConnection owner) : DbCommand
        {
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
            protected override DbParameter CreateDbParameter() => new FakeParameter();
            public override void Prepare() { }
            public override void Cancel() { }
            public override object ExecuteScalar()
            {
                Assert.Equal(SQLQueries.UPDATE_ClayzorOperationJournalState, CommandText);
                var id = Parameter<Guid>("AttemptId");
                if (!owner._journal.TryGetValue(id, out var current) ||
                    current.EmployeeId != Parameter<int>("EmployeeId") || current.Scope != Parameter<string>("ConflictKey") ||
                    current.State != (InventoryOperationState)Parameter<byte>("ExpectedState"))
                    return 0;
                owner._journal[id] = current with { State = (InventoryOperationState)Parameter<byte>("NextState") };
                return 1;
            }
            public override int ExecuteNonQuery()
            {
                if (CommandText == SQLQueries.INSERT_ClayzorOperationJournalAttempt)
                {
                    var transaction = Assert.IsType<JournalTransaction>(DbTransaction);
                    transaction.Pending = new JournalEntry(Parameter<Guid>("AttemptId"), Parameter<int>("EmployeeId"),
                        Parameter<string>("ConflictKey"), InventoryOperationState.Reserved);
                    Assert.DoesNotContain(Address, transaction.Pending.Scope);
                    Assert.DoesNotContain("SlBl", Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName));
                    return 1;
                }
                Assert.Equal("dbo.sp_MailboxesSlBlSynchronize", CommandText);
                Assert.Contains(owner._journal.Values, entry => entry.State == InventoryOperationState.Dispatching);
                owner.Dispatches++;
                owner.CurrentText = owner.PartialText ?? (string)Parameters.Cast<DbParameter>().Single(p => p.ParameterName == "SlBl").Value!;
                if (owner.LoseDispatchReply) throw new TimeoutException("Ответ почтовой процедуры потерян.");
                return 1;
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (CommandText == SQLQueries.INSERT_ClayzorLegacyOperationEvidence)
                {
                    var transaction = Assert.IsType<JournalTransaction>(DbTransaction);
                    transaction.Legacy = new JournalEntry(Parameter<Guid>("AttemptId"), Parameter<int>("EmployeeId"),
                        Parameter<string>("ConflictKey"), InventoryOperationState.Unknown);
                    return Row(("OwnEvidence", true));
                }
                if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalOpenSlot ||
                    CommandText == SQLQueries.SELECT_ClayzorOperationJournalOpenSlotForUpdate)
                    return Row(("Pending", owner._journal.Values.Concat(owner._legacy.Values).Any(entry => entry.Scope == Parameter<string>("ConflictKey") &&
                        entry.State is InventoryOperationState.Reserved or InventoryOperationState.Dispatching or InventoryOperationState.Unknown)));
                if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalAttemptForUpdate)
                {
                    var table = new DataTable();
                    table.Columns.Add("AttemptId", typeof(Guid));
                    table.Columns.Add("EmployeeId", typeof(int));
                    table.Columns.Add("ConflictKey", typeof(string));
                    table.Columns.Add("CommandKind", typeof(string));
                    table.Columns.Add("State", typeof(byte));
                    table.Columns.Add("ResultEntityId", typeof(int));
                    if (owner._journal.TryGetValue(Parameter<Guid>("AttemptId"), out var found))
                        table.Rows.Add(found.AttemptId, found.EmployeeId, found.Scope, "slbl.sync", (byte)found.State, DBNull.Value);
                    return table.CreateDataReader();
                }
                if (CommandText.Contains("SUSER_SID()")) return Row(("КодСотрудника", 4));
                if (CommandText.Contains("sys.parameters")) return Row(("Available", true));
                if (CommandText.Contains("SavedWords"))
                {
                    owner.ComparisonReads++;
                    var proposed = (string)Parameters.Cast<DbParameter>().Single(p => p.ParameterName == "Text").Value!;
                    return owner.CurrentText == proposed
                        ? Row(("NotDeleted", ""), ("NotSaved", ""))
                        : Row(("NotDeleted", owner.CurrentText), ("NotSaved", proposed));
                }
                if (CommandText.Contains("AS SafeList"))
                {
                    if (owner.FailReadAfterDispatch && owner.Dispatches > 0)
                        throw new InvalidOperationException("Тестовый сбой перечитывания");
                    return Row(("SafeList", owner.CurrentText), ("BlockedList", ""));
                }
                Assert.Contains("vwMailBoxesAddresses", CommandText);
                return Row(("Address", Address));
            }

            private T Parameter<T>(string name) => Assert.IsType<T>(Parameters[name].Value);

            private static DbDataReader Row(params (string Name, object Value)[] values)
            {
                var table = new DataTable();
                foreach (var (name, value) in values) table.Columns.Add(name, value.GetType());
                table.Rows.Add(values.Select(value => value.Value).ToArray());
                return table.CreateDataReader();
            }
        }
    }
}
