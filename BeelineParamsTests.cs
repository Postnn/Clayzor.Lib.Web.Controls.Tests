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
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Telefon.Beeline;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет строковый договор, ровно два параметра записи и сценарии карточки Билайн.</summary>
public sealed class BeelineParamsTests
{
    private const string Number = "000AB-001";
    private static readonly string[] Zones = ["Russian Standard Time", "Kaliningrad Standard Time", "Ekaterinburg Standard Time"];
    private static readonly DateTime Actual = new(2026, 11, 25, 23, 59, 59);
    private static readonly BeelineContract Original = new(Number, Number, "", "", null, "Тестовая компания", "",
        Actual, Zones[0], "initial@example.test", false, 4, Actual.AddHours(-1), "Тестовый инженер");

    /// <summary>Legacy email допускает +, апостроф и регистр. Пробелы и некорректный адрес отклоняются.</summary>
    [Theory]
    [InlineData("A+Alert@example.test", true)]
    [InlineData("o'connor@example.test", true)]
    [InlineData("user@example.test ", false)]
    [InlineData("user@example.test\n", false)]
    [InlineData("bad@@example.test", false)]
    [InlineData("user😀@example.test", false)]
    public void EmailUsesLegacyPatternAndSqlEncoding(string email, bool accepted) =>
        Assert.Equal(accepted, BeelineRules.Validate(new BeelineDraft(Zones[0], email), Zones).Count == 0);

    /// <summary>Проверяется точное конфигурационное значение и граница varchar(50).</summary>
    [Fact]
    public void ServerValidationRejectsUnknownZoneAndOverlongAddress()
    {
        Assert.True(BeelineRules.ValidNumber(Number));
        Assert.False(BeelineRules.ValidNumber(new string('0', 51)));
        Assert.False(BeelineRules.ValidNumber("\t"));
        Assert.Empty(BeelineRules.Validate(new BeelineDraft(Zones[0], new string('x', 40) + "@a.example"), Zones));
        Assert.Equal(2, BeelineRules.Validate(new BeelineDraft("Europe/Moscow", new string('x', 41) + "@a.example"), Zones).Count);
        Assert.Equal(2, BeelineRules.Validate(new BeelineDraft("", ""), Zones).Count);
    }

    /// <summary>Прямой вызов проверяет изменённую строку, роль, область роли и существование записи.</summary>
    [Theory]
    [InlineData("stale")]
    [InlineData("deleted")]
    [InlineData("viewer")]
    [InlineData("scoped")]
    [InlineData("principal")]
    public async Task ServerRejectsStaleOrUnauthorizedUpdate(string condition)
    {
        using var sql = new Probe();
        if (condition == "stale") sql.Row = Original with { EmailManager = "other@example.test" };
        if (condition == "deleted") sql.Row = null;
        if (condition == "viewer") sql.Role = Роли.Наблюдатель_услуг_связи;
        if (condition == "scoped") sql.PersonScope = 1;
        var expected = condition == "principal" ? 5 : 4;
        using var db = Db(sql);
        await Assert.ThrowsAsync<BeelineRejectedException>(() =>
            BeelineData.SaveAsync(db, "fixture", expected, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, Guid.NewGuid(), default));
        Assert.Equal(0, sql.Updates);
        Assert.Equal(0, sql.Commits);
    }

    /// <summary>Если триггер вернул параметры основного договора, UPDATE откатывается.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MismatchedReadBackOrZeroRowsCannotCommit(bool overwriteByTrigger)
    {
        using var sql = new Probe { OverrideParameters = overwriteByTrigger, Affected = overwriteByTrigger ? 1 : 0 };
        using var db = Db(sql);
        await Assert.ThrowsAsync<BeelineRejectedException>(() =>
            BeelineData.SaveAsync(db, "fixture", 4, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, Guid.NewGuid(), default));
        Assert.Equal(1, sql.Updates);
        Assert.Equal(0, sql.Commits);
        Assert.Equal(Original, sql.Row);
    }

    /// <summary>Статус факта выполнения не угадывается после потерянного ответа COMMIT.</summary>
    [Fact]
    public async Task CommitTimeoutStaysUnknown()
    {
        using var sql = new Probe { LoseCommit = true };
        using var db = Db(sql);
        await Assert.ThrowsAsync<BeelineUnknownException>(() =>
            BeelineData.SaveAsync(db, "fixture", 4, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, Guid.NewGuid(), default));
        Assert.Equal(1, sql.Updates);
        Assert.Equal(1, sql.Commits);
    }

    /// <summary>Потерянный ответ COMMIT подтверждается тем же идентификатором без нового UPDATE.</summary>
    [Fact]
    public async Task CommittedAttemptRereadsAfterLostResponseWithoutUpdatingAgain()
    {
        using var sql = new Probe { LoseCommit = true };
        using var db = Db(sql);
        var attempt = Guid.NewGuid();
        var draft = new BeelineDraft(Zones[1], "new@example.test");
        await Assert.ThrowsAsync<BeelineUnknownException>(() =>
            BeelineData.SaveAsync(db, "fixture", 4, Original, draft, Zones, attempt, default));

        var result = await BeelineData.ReadReceiptAsync(db, "fixture", 4, Number, default);
        Assert.Equal(new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, null), result.Receipt);
        Assert.False(result.HasPending);

        var saved = await BeelineData.SaveAsync(db, "fixture", 4, Original, draft, Zones, attempt, default);
        Assert.Equal(draft.EmailManager, Assert.IsType<BeelineContract>(saved).EmailManager);
        Assert.Equal(1, sql.Updates);
    }

    /// <summary>Попытка другого сотрудника блокирует новую запись, но не раскрывает его результат.</summary>
    [Fact]
    public async Task UnresolvedAttemptBlocksNewGuidAcrossEmployees()
    {
        var attempt = Guid.NewGuid();
        using var sql = new Probe();
        sql.Journal.Add(attempt, new JournalRow(attempt, 5, "beeline:contract:" + Number, "beeline.update",
            (byte)InventoryOperationState.Reserved));
        using var db = Db(sql);
        var result = await BeelineData.ReadReceiptAsync(db, "fixture", 4, Number, default);
        Assert.True(result.HasPending);
        Assert.Null(result.Receipt);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            BeelineData.SaveAsync(db, "fixture", 4, Original, new BeelineDraft(Zones[1], "new@example.test"),
                Zones, Guid.NewGuid(), default));
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Прежний GUID переносится только как свидетельство, блокирующее повторную запись.</summary>
    [Fact]
    public async Task OpeningRamEditorImportsLegacyAttemptAsUnresolved()
    {
        using var sql = new Probe();
        var attemptId = Guid.NewGuid();
        ClayLegacyDraftOperationEvidence[] evidence =
            [new(attemptId.ToString("N"), "beeline.update", Number)];
        await using var context = Configure(sql, legacyEvidence: evidence);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Исход предыдущей операции не подтверждён", card.Markup));
        card.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]));

        Assert.Equal(new JournalRow(attemptId, 4, "beeline:contract:" + Number, "beeline.update",
            (byte)InventoryOperationState.Unknown), Assert.Single(sql.LegacyEvidence.Values));
        Assert.Empty(sql.Journal);
        Assert.Equal(0, sql.Updates);
        var read = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]);
        Assert.Equal(new object[] { "beeline-parameters", Number, "4" }, read.Arguments.Take(3));
        Assert.True(Guid.TryParseExact(Assert.IsType<string>(read.Arguments[3]), "N", out _));
        Assert.Equal(read.Arguments[3],
            Assert.Single(context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]).Arguments[0]);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
    }

    /// <summary>Данные другого договора из interop не переносятся и не разблокируют запись.</summary>
    [Fact]
    public async Task LegacyEvidenceFromAnotherContractCannotOpenAnEditableRamDraft()
    {
        using var sql = new Probe();
        ClayLegacyDraftOperationEvidence[] evidence =
            [new(Guid.NewGuid().ToString("N"), "beeline.update", "other")];
        await using var context = Configure(sql, legacyEvidence: evidence);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]));

        Assert.Empty(sql.LegacyEvidence);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Обычный договор скрывает МБ. Сохранение берёт последний символ и обновляет аудит.</summary>
    [Fact]
    public async Task ExistingEditorUpdatesOnlyTwoFields()
    {
        using var sql = new Probe();
        await using var context = Configure(sql);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains(Number, card.Markup));
        Assert.DoesNotContain("Номер телефона МБ:", card.Markup);
        Assert.Contains("23:59:59", card.Markup);
        Assert.Equal(Zones, card.FindComponent<ClayDropDownList<string, string>>().Instance.Items);
        await card.FindAll("input").Last().InputAsync(new ChangeEventArgs { Value = "last+symbol@example.test" });
        await Button(card, "Сохранить").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, sql.Commits);
        var invocation = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[1]));
        var serverAttempt = Assert.Single(sql.Journal.Values);
        Assert.Equal(serverAttempt.AttemptId.ToString("N"), payload.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal((byte)InventoryOperationState.Confirmed, serverAttempt.State);
        Assert.Equal(Original with { EmailManager = "last+symbol@example.test", ChangedAt = Original.ChangedAt.AddSeconds(1) }, sql.Row);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }

    /// <summary>Мобильный бюджет виден даже без найденной активной SIM.</summary>
    [Fact]
    public async Task MissingEquipmentReportsErrorWithoutAWrite()
    {
        using var sql = new Probe { Row = Original with { BudgetNumber = Number, BudgetPhone = "79000000000", UserName = "Тест" } };
        await using var context = Configure(sql);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Мобильный бюджет", card.Markup));
        await card.FindAll("a").Single(link => link.TextContent.Contains("Номер телефона МБ")).ClickAsync(new MouseEventArgs());
        Assert.Contains("не удалось определить код оборудования", card.Markup);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Исторический пояс отображается при отсутствии настроенного списка, а запись недоступна.</summary>
    [Fact]
    public async Task MissingConfigurationPreservesExistingZone()
    {
        using var sql = new Probe { Row = Original with { TimeZone = "Historic Standard Time" } };
        await using var context = Configure(sql, includeZones: false);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Список Time Zone не настроен", card.Markup));
        var dropdown = card.FindComponent<ClayDropDownList<string, string>>();
        Assert.Equal("Historic Standard Time", dropdown.Instance.Value);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
    }

    /// <summary>Карточка оборудования открывается по подтверждённому ID и сохраняет локальный email.</summary>
    [Fact]
    public async Task ActiveSimLinksToEquipmentAndPreservesDraft()
    {
        using var sql = new Probe { Row = Original with
        {
            EquipmentId = 27, BudgetNumber = Number, BudgetPhone = "79000000000", UserName = "Тест",
        } };
        await using var context = Configure(sql);
        context.JSInterop.Setup<bool>("clayFormWindow.open", _ => true).SetResult(true);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Мобильный бюджет", card.Markup));
        await card.FindAll("input").Last().InputAsync(new ChangeEventArgs { Value = "draft@example.test" });
        await card.FindAll("a").Single(link => link.TextContent.Contains("Номер телефона МБ")).ClickAsync(new MouseEventArgs());
        var open = Assert.Single(context.JSInterop.Invocations["clayFormWindow.open"]);
        Assert.EndsWith("/Equipment/EquipmentForm?id=27", Assert.IsType<string>(open.Arguments[0]));
        Assert.Equal("beeline-sim-27", open.Arguments[1]);
        Assert.Contains("draft@example.test", card.Markup);
        Assert.False(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Новый экземпляр без доступа к договору не получает черновик прежнего документа.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("sql229")]
    [InlineData("policy")]
    public async Task MissingOrDeniedContractDoesNotRestorePriorDraft(string failure)
    {
        using var sql = new Probe
        {
            Row = failure == "missing" ? null : Original,
            CanRead = failure != "policy",
            DenyRead = failure == "sql229",
        };
        var snapshot = RecoverySnapshot("local@example.test");
        await using var context = Configure(sql, restore: new ClayDraftRestore("beeline-recovery", true, 4, null, null,
            new Dictionary<string, string> { ["main"] = snapshot }), pendingRaw: true);
        var card = context.Render<BeelineParamsForm>();

        card.WaitForAssertion(() => Assert.Contains(failure == "missing" ? "Договор Билайн не найден" : "Нет доступа", card.Markup));
        Assert.DoesNotContain("local@example.test", card.Markup);
        Assert.DoesNotContain("Тестовая компания", card.Markup);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.True(Button(card, "OK").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Обновление после появления договора начинает с актуальной строки БД.</summary>
    [Fact]
    public async Task MissingContractCanBeRetriedFromDatabase()
    {
        using var sql = new Probe { Row = null };
        await using var context = Configure(sql);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Договор Билайн не найден или недоступен", card.Markup));

        sql.Row = Original with { EmailManager = "fresh@example.test" };
        Assert.False(Button(card, "Обновить").HasAttribute("disabled"));
        await Button(card, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("fresh@example.test", card.Markup);
        var registration = Assert.Single(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Equal(new object[] { "beeline-parameters", Number, "4" }, registration.Arguments.Take(3));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Совпадение возвращённого договора с попыткой допускает только сверку.</summary>
    [Fact]
    public async Task UnknownIsPreservedWhenFreshValuesMatch()
    {
        using var sql = new Probe();
        var snapshot = RecoverySnapshot("attempt@example.test");
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "beeline-attempt", kind = "beeline.update", status = "unknown", revision = 7,
            data = snapshot, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = Configure(sql, restore: new ClayDraftRestore("beeline-recovery", true, 7, null, operation,
            new Dictionary<string, string> { ["main"] = snapshot }));
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Обнаружена незавершённая операция", card.Markup));

        sql.Row = Original with { TimeZone = Zones[1], EmailManager = "attempt@example.test" };
        await Button(card, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("Данные в базе совпадают с отправленными", card.Markup);
        Assert.Contains("beeline-attempt", card.Markup);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Только серверное подтверждение того же GUID завершает принятую ревизию браузерной попытки.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshReconcilesOnlyTheExactServerAttempt(bool matchingAttempt)
    {
        const long submittedRevision = 7;
        const long laterRevision = 8;
        const string email = "attempt@example.test";
        const string laterEmail = "later@example.test";
        using var sql = new Probe
        {
            Row = Original with { TimeZone = Zones[1], EmailManager = email },
        };
        var localAttemptId = Guid.NewGuid();
        var serverAttemptId = matchingAttempt ? localAttemptId : Guid.NewGuid();
        sql.Journal.Add(serverAttemptId, new JournalRow(serverAttemptId, 4, "beeline:contract:" + Number,
            "beeline.update", (byte)InventoryOperationState.Confirmed));
        var operation = JsonSerializer.Serialize(new
        {
            version = 1,
            attemptId = localAttemptId.ToString("N"),
            kind = "beeline.update",
            status = "unknown",
            revision = submittedRevision,
            data = RecoverySnapshot(email),
            scopes = new[] { new { name = "main", typedRevision = submittedRevision, rawRevisions = new Dictionary<string, long>() } },
            acceptLegacyTyped = false,
            legacyTypedRevision = 0,
        });
        await using var context = Configure(sql, restore: new ClayDraftRestore("beeline-recovery", true,
            laterRevision, null, operation, new Dictionary<string, string> { ["main"] = RecoverySnapshot(laterEmail) }));
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Обнаружена незавершённая операция", card.Markup));

        await Button(card, "Обновить").ClickAsync(new MouseEventArgs());

        if (matchingAttempt)
        {
            Assert.Contains("База подтвердила запись", card.Markup);
            var confirmed = Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
            Assert.Equal(submittedRevision, confirmed.Arguments[1]);
            var commit = Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
            Assert.Equal(submittedRevision, commit.Arguments[1]);
        }
        else
        {
            Assert.Contains("Данные в базе совпадают с отправленными", card.Markup);
            Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        }

        Assert.Contains(laterEmail, card.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Сохранённый снимок недоступен по невалидному номеру или без сотрудника.</summary>
    [Theory]
    [InlineData(" ", true)]
    [InlineData(Number, false)]
    public async Task InvalidNumberOrIdentityDoesNotOpenDraft(string id, bool authenticated)
    {
        using var sql = new Probe();
        await using var context = Configure(sql, id: id, authenticated: authenticated);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains(authenticated ? "Некорректный номер договора" : "Не удалось подтвердить текущего сотрудника", card.Markup));

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Ошибки подготовки завершают конкретный marker, отправленный UPDATE при потере ответа остаётся Unknown.</summary>
    [Theory]
    [InlineData("open", false)]
    [InlineData("begin", false)]
    [InlineData("access", false)]
    [InlineData("locked", false)]
    [InlineData("dispatch", true)]
    [InlineData("commit", true)]
    public async Task SaveClassifiesFailureByDispatchBoundary(string fault, bool unknown)
    {
        using var sql = new Probe { LoseCommit = fault == "commit" };
        await using var context = Configure(sql);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("initial@example.test", card.Markup));
        await card.FindAll("input").Last().InputAsync(new ChangeEventArgs { Value = "edited@example.test" });
        sql.FaultAfterLoad = fault;
        await Button(card, "Сохранить").ClickAsync(new MouseEventArgs());

        Assert.Contains(unknown ? "Исход операции требует сверки" : "Запись не начата", card.Markup);
        Assert.Contains("edited@example.test", card.Markup);
        Assert.Equal(fault is "dispatch" or "commit" ? 1 : 0, sql.Updates);
        Assert.Equal(unknown, Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        var marker = unknown ? "clayDraftStore.unknownOperation" : "clayDraftStore.rejectOperation";
        var other = unknown ? "clayDraftStore.rejectOperation" : "clayDraftStore.unknownOperation";
        Assert.Equal(2L, Assert.Single(context.JSInterop.Invocations[marker]).Arguments[1]);
        Assert.Empty(context.JSInterop.Invocations[other]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
    }

    /// <summary>Продолжение, сброс и закрытие используют только точную родительскую ревизию.</summary>
    [Theory]
    [InlineData("Продолжить редактирование", true)]
    [InlineData("Закрыть без сохранения", true)]
    [InlineData("Отменить мои изменения и загрузить из БД", true)]
    [InlineData("Закрыть без сохранения", false)]
    public async Task EditableCancelUsesExactRamRevisionForResetOrClose(string choice, bool closeAccepted)
    {
        using var sql = new Probe();
        await using var context = Configure(sql);
        var dialogs = context.Render<MudDialogProvider>();
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(31, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(closeAccepted);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains(Original.EmailManager, card.Markup));
        await card.InvokeAsync(() => card.Find("input[data-draft-key='beeline.email']")
            .InputAsync(new ChangeEventArgs { Value = "local@example.test" }));

        var cancel = PressCancelAsync(card);
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        var fresh = Original with { EmailManager = "fresh@example.test", ChangedAt = Original.ChangedAt.AddSeconds(1) };
        sql.Row = fresh;
        await ChooseCancelAsync(dialogs, choice);
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        var calls = context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        var closeCalls = context.JSInterop.Invocations["clayDraftStore.closeRam"];
        if (choice == "Продолжить редактирование")
        {
            Assert.Empty(calls);
            Assert.Empty(closeCalls);
            Assert.Contains("local@example.test", card.Markup);
            Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        }
        else if (choice == "Закрыть без сохранения")
        {
            Assert.Empty(calls);
            Assert.Equal(31L, Assert.Single(closeCalls).Arguments[1]);
            if (closeAccepted)
                Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
            else
            {
                Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
                Assert.Contains("local@example.test", card.Markup);
                Assert.Contains("Ввод изменился", card.Markup);
            }
        }
        else
        {
            var replacement = Assert.Single(calls);
            Assert.Equal("main", replacement.Arguments[1]);
            Assert.Equal(31L, replacement.Arguments[2]);
            using var snapshot = JsonDocument.Parse(Assert.IsType<string>(replacement.Arguments[3]));
            Assert.Equal(Number, snapshot.RootElement.GetProperty("Version").GetProperty("Key").GetString());
            Assert.Equal(fresh.ChangedAt, snapshot.RootElement.GetProperty("Version").GetProperty("ChangedAt").GetDateTime());
            Assert.Contains("fresh@example.test", card.Markup);
            Assert.Empty(closeCalls);
            Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        }
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Недостаточные права, другой актор, пропавший или иной договор и поздний raw защищают ввод.</summary>
    [Theory]
    [InlineData("actor")]
    [InlineData("rights")]
    [InlineData("missing")]
    [InlineData("number")]
    [InlineData("late")]
    public async Task EditableReset_FailedFreshReadOrScopeVersionKeepsInput(string failure)
    {
        using var sql = new Probe();
        await using var context = Configure(sql);
        var dialogs = context.Render<MudDialogProvider>();
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(31, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(false);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains(Original.EmailManager, card.Markup));
        await card.InvokeAsync(() => card.Find("input[data-draft-key='beeline.email']")
            .InputAsync(new ChangeEventArgs { Value = "local@example.test" }));

        var cancel = PressCancelAsync(card);
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        if (failure == "actor") sql.ActorId = 8;
        if (failure == "rights") sql.Role = Роли.Наблюдатель_услуг_связи;
        if (failure == "missing") sql.Row = null;
        if (failure == "number") sql.Row = Original with { Number = "DIFFERENT" };
        await ChooseCancelAsync(dialogs, "Отменить мои изменения и загрузить из БД");
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("local@example.test", card.Markup);
        Assert.Equal(failure == "late" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Новый просмотр без доступного договора закрывается без действий над чужим контекстом.</summary>
    [Fact]
    public async Task MissingContractClosesWithoutTouchingDraftStorage()
    {
        using var sql = new Probe { Row = null };
        await using var context = Configure(sql);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Договор Билайн не найден или недоступен", card.Markup));

        await PressCancelAsync(card).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, sql.Updates);
    }

    private static Task PressCancelAsync(IRenderedComponent<BeelineParamsForm> card) =>
        card.InvokeAsync(() => Button(card, "Отмена").ClickAsync(new MouseEventArgs()));

    private static Task ChooseCancelAsync(IRenderedComponent<MudDialogProvider> dialogs, string choice) =>
        dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == choice).ClickAsync(new MouseEventArgs()));

    /// <summary>Новый документ без договора не открывает сохранённый браузерный снимок команды.</summary>
    [Fact]
    public async Task MissingContractDoesNotExposeLegacySubmittedSnapshot()
    {
        using var sql = new Probe { Row = null };
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "beeline-submitted", kind = "beeline.update", status = "unknown",
            revision = 18, data = RecoverySnapshot("submitted@example.test"), scopes = Array.Empty<object>(),
            acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = Configure(sql, restore: new ClayDraftRestore("beeline-recovery", true, 18, null, operation,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot("local@example.test") }));
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Договор Билайн не найден или недоступен", card.Markup));

        Assert.DoesNotContain("Посмотреть снимок попытки", card.Markup);
        Assert.DoesNotContain("submitted@example.test", card.Markup);
        Assert.DoesNotContain("local@example.test", card.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, sql.Updates);
    }

    private static string RecoverySnapshot(string email) => JsonSerializer.Serialize(new
    {
        Original = new BeelineDraft(Original.TimeZone, Original.EmailManager),
        Current = new BeelineDraft(Zones[1], email),
    });

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<BeelineParamsForm> card, string label) =>
        card.FindAll("button").Single(button => button.TextContent.Trim() == label);

    private static DbManager Db(Probe connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
    }

    private static BunitContext Configure(Probe sql, bool includeZones = true, ClayDraftRestore? restore = null,
        bool pendingRaw = false, string id = Number, bool authenticated = true,
        ClayLegacyDraftOperationEvidence[]? legacyEvidence = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(restore ?? new ClayDraftRestore("beeline-fixture", false, 0, null, null));
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(true);
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>(
            "clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true).SetResult(legacyEvidence ?? []);
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], pendingRaw ? ["data:beeline.email"] : []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(!pendingRaw, pendingRaw ? ["main"] : [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 0, 0));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"fixture","kind":"beeline.update","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddScoped(_ => Db(sql));
        context.Services.AddScoped(_ => new ClayDraftService(new JournalJs(context.JSInterop.JSRuntime)));
        context.Services.AddScoped<ClayFormWindowService>();
        var settings = new ConfigurationBuilder();
        if (includeZones)
            settings.AddInMemoryCollection(Zones.Select((zone, index) =>
                new KeyValuePair<string, string?>($"BeelineTimeZones:{index}", zone)));
        context.Services.AddSingleton<IConfiguration>(settings.Build());
        context.Services.AddSingleton<AuthenticationStateProvider>(new FixtureAuthentication(authenticated));
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/Telefon/BeelineParams?id=" + Uri.EscapeDataString(id));
        return context;
    }

    private sealed class FixtureAuthentication(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(authenticated ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test") : new ClaimsIdentity())));
    }

    private sealed class JournalJs(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            var result = await inner.InvokeAsync<T>(identifier, token, args);
            if (identifier != "clayDraftStore.beginOperationIntent")
                return result;

            using var payload = JsonDocument.Parse((string)args![1]!);
            var intent = payload.RootElement;
            var response = JsonSerializer.Serialize(new
            {
                version = 1,
                attemptId = intent.GetProperty("attemptId").GetString(),
                kind = intent.GetProperty("kind").GetString(),
                status = "running",
                revision = 2,
                data = intent.GetProperty("data").GetString(),
                scopes = new[] { new { name = "main", typedRevision = 0, rawRevisions = new Dictionary<string, long>() } },
                acceptLegacyTyped = false,
                legacyTypedRevision = 0,
            });
            return (T)(object)response;
        }
    }

    private sealed record JournalRow(Guid AttemptId, int EmployeeId, string ConflictKey, string CommandKind, byte State);

    private sealed class Probe : DbConnection
    {
        public int ActorId { get; set; } = 4;
        public Роли Role { get; set; } = Роли.Администратор_тарифов_связи;
        public int PersonScope { get; set; }
        public int Affected { get; init; } = 1;
        public bool OverrideParameters { get; init; }
        public bool LoseCommit { get; init; }
        public bool CanRead { get; set; } = true;
        public bool DenyRead { get; init; }
        public string? FaultAfterLoad { get; set; }
        public Dictionary<Guid, JournalRow> Journal { get; set; } = [];
        public Dictionary<Guid, JournalRow> LegacyEvidence { get; set; } = [];
        public int Commits { get; set; }
        public int Updates { get; set; }
        public BeelineContract? Row { get; set; } = Original;
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => FaultAfterLoad == "open" ? ConnectionState.Closed : ConnectionState.Open;
        public override void Open()
        {
            if (FaultAfterLoad == "open") throw new TimeoutException("Тестовый сбой открытия соединения");
        }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            Assert.Equal(IsolationLevel.Serializable, level);
            if (FaultAfterLoad == "begin") throw new TimeoutException("Тестовый сбой начала транзакции");
            return new ProbeTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new Command(this);
    }

    private sealed class ProbeTransaction(Probe owner) : DbTransaction
    {
        internal BeelineContract? Row { get; set; } = owner.Row;
        internal Dictionary<Guid, JournalRow> Journal { get; } = new(owner.Journal);
        internal Dictionary<Guid, JournalRow> LegacyEvidence { get; } = new(owner.LegacyEvidence);
        internal bool BusinessWrite { get; set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => owner;
        public override void Commit()
        {
            owner.Row = Row;
            owner.Journal = Journal;
            owner.LegacyEvidence = LegacyEvidence;
            if (!BusinessWrite)
                return;
            owner.Commits++;
            if (owner.LoseCommit) throw new TimeoutException();
        }
        public override void Rollback() { }
    }

    private sealed class Command(Probe owner) : DbCommand
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
            using var reader = ReadJournal();
            Assert.True(reader.Read());
            return reader.GetValue(0);
        }
        public override int ExecuteNonQuery()
        {
            Assert.Equal(SQLQueries.INSERT_ClayzorOperationJournalAttempt, CommandText);
            var tx = Assert.IsType<ProbeTransaction>(DbTransaction);
            var id = (Guid)Parameter("AttemptId").Value!;
            tx.Journal.Add(id, new JournalRow(id, (int)Parameter("EmployeeId").Value!,
                (string)Parameter("ConflictKey").Value!, (string)Parameter("CommandKind").Value!,
                (byte)InventoryOperationState.Reserved));
            Assert.DoesNotContain(Parameters.Cast<DbParameter>(), parameter =>
                parameter.ParameterName.Contains("email", StringComparison.OrdinalIgnoreCase) ||
                parameter.ParameterName.Contains("timeZone", StringComparison.OrdinalIgnoreCase));
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("SUSER_SID()"))
                return Rows([("КодСотрудника", typeof(int))], [owner.ActorId]).CreateDataReader();
            if (CommandText.Contains("fn_ТекущиеРоли"))
                return Rows([("RoleId", typeof(int)), ("PersonId", typeof(int))], [(int)owner.Role, owner.PersonScope]).CreateDataReader();
            if (CommandText.Contains("HAS_PERMS_BY_NAME"))
            {
                if (DbTransaction is not null && owner.FaultAfterLoad == "access")
                    throw SqlExceptionFactory.Create(-2, "Тестовый таймаут проверки разрешений");
                return Rows([("CanRead", typeof(bool)), ("CanUpdate", typeof(bool)), ("NamedWriter", typeof(bool))],
                    [owner.CanRead, true, false]).CreateDataReader();
            }

            if (CommandText.Contains("dbo.ClayzorOperationJournal", StringComparison.Ordinal) ||
                CommandText == SQLQueries.INSERT_ClayzorLegacyOperationEvidence)
                return ReadJournal();

            var id = Parameter("id");
            Assert.Equal(Number, id.Value);
            Assert.Equal(DbType.String, id.DbType);
            Assert.Equal(50, id.Size);
            if (CommandText.StartsWith("UPDATE ", StringComparison.Ordinal))
            {
                var tx = Assert.IsType<ProbeTransaction>(DbTransaction);
                Assert.Equal(new[] { "email", "id", "timeZone" }, Parameters.Cast<DbParameter>().Select(p => p.ParameterName).Order().ToArray());
                owner.Updates++;
                tx.BusinessWrite = true;
                if (owner.FaultAfterLoad == "dispatch")
                    throw SqlExceptionFactory.Create(-2, "Тестовая потеря ответа UPDATE");
                if (owner.Affected > 0 && !owner.OverrideParameters)
                    tx.Row = tx.Row! with { TimeZone = (string)Parameter("timeZone").Value!,
                        EmailManager = (string)Parameter("email").Value!, ChangedAt = tx.Row!.ChangedAt.AddSeconds(1) };
                return Rows([("Affected", typeof(int))], [owner.Affected]).CreateDataReader();
            }

            if (owner.DenyRead && DbTransaction is null) throw SqlExceptionFactory.Create(229, "Тестовый отказ SELECT");
            Assert.Equal((int)Лица.Билайн, Parameter("beeline").Value);
            if (DbTransaction is not null)
            {
                Assert.Contains("UPDLOCK", CommandText);
                if (owner.FaultAfterLoad == "locked")
                    throw SqlExceptionFactory.Create(-2, "Тестовый таймаут чтения под блокировкой");
            }
            var row = DbTransaction is ProbeTransaction transaction ? transaction.Row : owner.Row;
            return Rows([
                ("Number", typeof(string)), ("BaseNumber", typeof(string)), ("BudgetNumber", typeof(string)),
                ("BudgetPhone", typeof(string)), ("EquipmentId", typeof(int)), ("Customer", typeof(string)),
                ("UserName", typeof(string)), ("Actual", typeof(DateTime)), ("TimeZone", typeof(string)),
                ("EmailManager", typeof(string)), ("HasAccount", typeof(bool)), ("ChangedBy", typeof(int)),
                ("ChangedAt", typeof(DateTime)), ("ChangedByName", typeof(string))],
                row is null ? null : [row.Number, row.BaseNumber, row.BudgetNumber, row.BudgetPhone, row.EquipmentId,
                    row.Customer, row.UserName, row.Actual, row.TimeZone, row.EmailManager, row.HasAccount,
                    row.ChangedBy, row.ChangedAt, row.ChangedByName]).CreateDataReader();
        }

        private DbDataReader ReadJournal()
        {
            var journal = DbTransaction is ProbeTransaction tx ? tx.Journal : owner.Journal;
            var evidence = DbTransaction is ProbeTransaction evidenceTx ? evidenceTx.LegacyEvidence : owner.LegacyEvidence;
            if (CommandText == SQLQueries.INSERT_ClayzorLegacyOperationEvidence)
            {
                Assert.IsType<ProbeTransaction>(DbTransaction);
                Assert.Equal(new[] { "AttemptId", "CommandKind", "ConflictKey", "EmployeeId" },
                    Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName).Order(StringComparer.Ordinal).ToArray());
                var attemptId = (Guid)Parameter("AttemptId").Value!;
                var candidate = new JournalRow(attemptId, (int)Parameter("EmployeeId").Value!,
                    (string)Parameter("ConflictKey").Value!, (string)Parameter("CommandKind").Value!,
                    (byte)InventoryOperationState.Unknown);
                var owned = evidence.TryAdd(attemptId, candidate) || evidence[attemptId] == candidate;
                return Rows([("Owned", typeof(bool))], [owned]).CreateDataReader();
            }
            if (CommandText == SQLQueries.UPDATE_ClayzorOperationJournalState)
            {
                var attemptId = (Guid)Parameter("AttemptId").Value!;
                var state = (byte)Parameter("NextState").Value!;
                Assert.Equal((byte)Parameter("ExpectedState").Value!, journal[attemptId].State);
                Assert.Equal(state == (byte)InventoryOperationState.Confirmed, DbTransaction is not null);
                journal[attemptId] = journal[attemptId] with { State = state };
                return Rows([("Affected", typeof(int))], [1]).CreateDataReader();
            }
            if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalOpenSlotForUpdate ||
                CommandText == SQLQueries.SELECT_ClayzorOperationJournalOpenSlot)
            {
                var key = (string)Parameter("ConflictKey").Value!;
                return Rows([("Found", typeof(bool))], [journal.Values.Any(row => row.ConflictKey == key &&
                    row.State == (byte)InventoryOperationState.Reserved) ||
                    evidence.Values.Any(row => row.ConflictKey == key)]).CreateDataReader();
            }
            var attempt = Parameters.Cast<DbParameter>().Any(parameter => parameter.ParameterName.TrimStart('@') == "AttemptId")
                ? journal.GetValueOrDefault((Guid)Parameter("AttemptId").Value!)
                : journal.Values.LastOrDefault(row => row.EmployeeId == (int)Parameter("EmployeeId").Value! &&
                    row.ConflictKey == (string)Parameter("ConflictKey").Value!);
            return Rows([("AttemptId", typeof(Guid)), ("EmployeeId", typeof(int)), ("ConflictKey", typeof(string)),
                ("CommandKind", typeof(string)), ("State", typeof(byte)), ("ResultEntityId", typeof(int))],
                attempt is null ? null : [attempt.AttemptId, attempt.EmployeeId, attempt.ConflictKey, attempt.CommandKind,
                    attempt.State, null]).CreateDataReader();
        }

        private DbParameter Parameter(string name) =>
            Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == name);

        private static DataTable Rows((string Name, Type Type)[] columns, object?[]? values)
        {
            var table = new DataTable();
            foreach (var (name, type) in columns) table.Columns.Add(name, type);
            if (values is not null) table.Rows.Add(values.Select(value => value ?? DBNull.Value).ToArray());
            return table;
        }
    }
}

