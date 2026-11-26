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
            BeelineData.SaveAsync(db, "fixture", expected, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, default));
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
            BeelineData.SaveAsync(db, "fixture", 4, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, default));
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
            BeelineData.SaveAsync(db, "fixture", 4, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, default));
        Assert.Equal(1, sql.Updates);
        Assert.Equal(1, sql.Commits);
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

    /// <summary>Черновик двух параметров остаётся видимым при недоступном исходном договоре.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("sql229")]
    [InlineData("policy")]
    public async Task MissingOrDeniedContractShowsOnlyOwnedDraft(string failure)
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

        card.WaitForAssertion(() => Assert.Contains("local@example.test", card.Markup));
        Assert.Contains(Number, card.Markup);
        Assert.Contains(Zones[1], card.Markup);
        Assert.DoesNotContain("Тестовая компания", card.Markup);
        var email = card.Find("section[data-draft-scope=main] input[data-draft-key='beeline.email']");
        Assert.True(email.HasAttribute("readonly"));
        Assert.False(email.HasAttribute("disabled"));
        Assert.Equal("local@example.test", email.GetAttribute("value"));
        Assert.Contains(context.JSInterop.Invocations["clayDraftStore.applyRaw"], call => Equals(call.Arguments[1], "main"));
        Assert.True(card.Find("section[data-draft-scope=main] input[data-draft-key='beeline.zone']").HasAttribute("readonly"));
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.True(Button(card, "OK").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Equal(new object[] { "beeline-parameters", Number, "4" },
            Assert.Single(context.JSInterop.Invocations["clayDraftStore.register"]).Arguments.Take(3));
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Повторное чтение в открытой карточке показывает свежую версию рядом с собственным черновиком.</summary>
    [Fact]
    public async Task MissingContractCanBeRetriedWithoutReplacingOwnDraft()
    {
        using var sql = new Probe { Row = null };
        await using var context = Configure(sql, restore: new ClayDraftRestore("beeline-recovery", true, 4, null, null,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot("local@example.test") }));
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("local@example.test", card.Markup));

        await Button(card, "Повторить чтение договора").ClickAsync(new MouseEventArgs());
        Assert.Contains("Договор Билайн не найден или недоступен", card.Markup);

        sql.Row = Original with { EmailManager = "fresh@example.test" };
        Assert.False(Button(card, "Обновить").HasAttribute("disabled"));
        await Button(card, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("fresh@example.test", card.Markup);
        sql.CanRead = false;
        await Button(card, "Повторить чтение договора").ClickAsync(new MouseEventArgs());
        Assert.DoesNotContain("fresh@example.test", card.Markup);
        Assert.Contains("Нет доступа к параметрам договора Билайн", card.Markup);
        Assert.Contains("local@example.test", card.Markup);
        Assert.True(card.Find("input[data-draft-key='beeline.email']").HasAttribute("readonly"));
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Совпадение возвращённого договора с попыткой допускает только сверку.</summary>
    [Fact]
    public async Task UnknownIsPreservedWhenContractReturns()
    {
        using var sql = new Probe { Row = null };
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

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
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
        Assert.Equal(unknown ? 1 : 0, sql.Updates);
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
    [InlineData("Продолжить редактирование")]
    [InlineData("Закрыть без сохранения")]
    [InlineData("Отменить мои изменения и загрузить из БД")]
    public async Task EditableCancel_DoesNotDiscardTheRegistration(string choice)
    {
        using var sql = new Probe();
        await using var context = Configure(sql);
        var dialogs = context.Render<MudDialogProvider>();
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(31, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
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
        if (choice == "Продолжить редактирование")
        {
            Assert.Empty(calls);
            Assert.Contains("local@example.test", card.Markup);
            Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        }
        else
        {
            var replacement = Assert.Single(calls);
            Assert.Equal("main", replacement.Arguments[1]);
            Assert.Equal(31L, replacement.Arguments[2]);
            if (choice == "Закрыть без сохранения")
            {
                Assert.Null(replacement.Arguments[3]);
                Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
            }
            else
            {
                using var snapshot = JsonDocument.Parse(Assert.IsType<string>(replacement.Arguments[3]));
                Assert.Equal(Number, snapshot.RootElement.GetProperty("Version").GetProperty("Key").GetString());
                Assert.Equal(fresh.ChangedAt, snapshot.RootElement.GetProperty("Version").GetProperty("ChangedAt").GetDateTime());
                Assert.Contains("fresh@example.test", card.Markup);
                Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
            }
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

    /// <summary>В readonly recovery все решения оставляют сырой ввод и прежний Unknown.</summary>
    [Theory]
    [InlineData("Продолжить редактирование", false)]
    [InlineData("Закрыть без сохранения", true)]
    [InlineData("Отменить мои изменения и загрузить из БД", true)]
    public async Task RecoveryCancel_PreservesRawAndPendingOperation(string choice, bool unknown)
    {
        using var sql = new Probe { Row = null };
        var snapshot = RecoverySnapshot("local@example.test");
        var operation = unknown ? JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "beeline-attempt", kind = "beeline.update", status = "unknown", revision = 7,
            data = snapshot, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        }) : null;
        await using var context = Configure(sql, pendingRaw: true, restore: new ClayDraftRestore(
            "beeline-recovery", true, 7, null, operation, new Dictionary<string, string> { ["main"] = snapshot }));
        var dialogs = context.Render<MudDialogProvider>();
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("local@example.test", card.Markup));
        card.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.getRestoreReadiness"]));

        var cancel = PressCancelAsync(card);
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        sql.Row = Original with { EmailManager = "fresh@example.test" };
        await ChooseCancelAsync(dialogs, choice);
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        if (choice == "Отменить мои изменения и загрузить из БД")
            Assert.Contains("fresh@example.test", card.Markup);
        Assert.Contains("local@example.test", card.Markup);
        Assert.True(card.Find("input[data-draft-key='beeline.email']").HasAttribute("readonly"));
        Assert.Equal(choice == "Закрыть без сохранения" ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
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

    /// <summary>Разрешённые поля видит только свежеподтверждённый владелец точного договора.</summary>
    [Theory]
    [InlineData("owned")]
    [InlineData("owned-denied")]
    [InlineData("wrong-key")]
    [InlineData("wrong-actor")]
    [InlineData("wrong-kind")]
    [InlineData("invalid-email")]
    public async Task SubmittedBeelineView_RequiresExactOwnerAndKey(string mode)
    {
        using var sql = new Probe { Row = mode == "owned-denied" ? Original : null, DenyRead = mode == "owned-denied" };
        const string submittedEmail = "submitted@example.test";
        var data = JsonSerializer.Serialize(new
        {
            Original = new BeelineDraft(Zones[0], "base@example.test"),
            Current = new BeelineDraft(Zones[1], mode == "invalid-email" ? "fixture-private-payload" : submittedEmail),
            Version = new { Key = mode == "wrong-key" ? "FOREIGN" : Number, ChangedBy = 4, ChangedAt = Actual },
            PrivateMetadata = "fixture-unrelated-wire-data",
        });
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "beeline-submitted", kind = mode == "wrong-kind" ? "phone-pool-attribute.update" : "beeline.update",
            status = "unknown", revision = 18, data, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = Configure(sql, restore: new ClayDraftRestore("beeline-recovery", true, 18, null, operation,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot("local@example.test") }));
        var page = context.Render<BeelineParamsForm>();
        page.WaitForAssertion(() => Assert.Contains("Посмотреть снимок попытки", page.Markup));
        if (mode == "wrong-actor") sql.ActorId = 8;

        await page.InvokeAsync(() => Button(page, "Посмотреть снимок попытки").ClickAsync());

        if (mode is "owned" or "owned-denied")
        {
            var panel = page.Find("[data-testid=submitted-attempt]");
            Assert.Contains(submittedEmail, panel.TextContent);
            Assert.Contains(Zones[1], panel.TextContent);
            Assert.DoesNotContain("local@example.test", panel.TextContent);
        }
        else
        {
            Assert.Empty(page.FindAll("[data-testid=submitted-attempt]"));
            Assert.DoesNotContain(submittedEmail, page.Markup);
        }
        Assert.DoesNotContain("fixture-private-payload", page.Markup);
        Assert.DoesNotContain("fixture-unrelated-wire-data", page.Markup);
        Assert.True(Button(page, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
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
        bool pendingRaw = false, string id = Number, bool authenticated = true)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(restore ?? new ClayDraftRestore("beeline-fixture", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], pendingRaw ? ["data:beeline.email"] : []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(!pendingRaw, pendingRaw ? ["main"] : [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 0, 0));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"fixture","kind":"beeline.update","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddScoped(_ => Db(sql));
        context.Services.AddScoped<ClayDraftService>();
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
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => owner;
        public override void Commit()
        {
            owner.Row = Row;
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
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();

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

            var id = Parameter("id");
            Assert.Equal(Number, id.Value);
            Assert.Equal(DbType.String, id.DbType);
            Assert.Equal(50, id.Size);
            if (CommandText.StartsWith("UPDATE ", StringComparison.Ordinal))
            {
                var tx = Assert.IsType<ProbeTransaction>(DbTransaction);
                Assert.Equal(new[] { "email", "id", "timeZone" }, Parameters.Cast<DbParameter>().Select(p => p.ParameterName).Order().ToArray());
                owner.Updates++;
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

