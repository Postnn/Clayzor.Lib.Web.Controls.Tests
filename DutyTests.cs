using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Duty;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Календарная модель и транзакция расписания с тестовым SQL-транспортом.</summary>
public sealed partial class DutyTests
{
    private static readonly DateOnly DutyDate = new(2026, 11, 21);
    private static readonly DateOnly OldDay = new(2026, 11, 28);
    private static readonly DateOnly NewDay = new(2026, 11, 29);
    private static readonly DateTime Today = new(2026, 11, 25);
    private static readonly DutyRow Original = new(DutyDate.ToDateTime(TimeOnly.MinValue), 8, 8, null, 0, null,
        OldDay.ToDateTime(TimeOnly.MinValue), 0, 4, new DateTime(2026, 11, 22), null, null, null);

    /// <summary>Разбирает однозначные ссылки грида без UTC-преобразования.</summary>
    [Theory]
    [InlineData("20261121", true)]
    [InlineData("2026-11-21", true)]
    [InlineData("21.11.2026 0:00:00", true)]
    [InlineData("11/21/2026 12:00:00 AM", true)]
    [InlineData("11/21/2026 00:00:00", true)]
    [InlineData("2026-11-21T00:00:00", true)]
    [InlineData("20260229", false)]
    [InlineData("04/05/2026", false)]
    [InlineData("2026-11-21T12:00:00", false)]
    public void RouteKeyUsesExplicitFormats(string value, bool accepted)
    {
        Assert.Equal(accepted, DutyKey.TryParse(value, out var date));
        if (accepted) Assert.Equal(DutyDate, date);
    }

    /// <summary>Старый незакрытый снимок без факта не назначает вариант «никто».</summary>
    [Fact]
    public void MissingActualRequiresExplicitConfirmation()
    {
        var draft = DutyDraft.FromRow(Original with { ActualId = null, TimeOff = null });
        Assert.Null(draft.Confirmation);
        Assert.Contains("выбрать вариант", draft.Validate(true, null));
        Assert.Contains("без подтверждения", (draft with { Confirmation = DutyConfirmation.Nobody }).Validate(true, null));
        Assert.Null((draft with { Confirmation = DutyConfirmation.Nobody, Close = true }).Validate(true, null));
    }

    /// <summary>Скрытый отгул не попадает в запись при рабочем дне или неподтверждённом флаге.</summary>
    [Fact]
    public void TimeOffFollowsEffectiveBranch()
    {
        var draft = new DutyDraft(DutyDate, 8, DutyConfirmation.Other, 9, true, DutyDate, true);
        Assert.Equal(9, draft.EffectiveEngineer(true));
        Assert.Contains("больше", draft.Validate(true, false));
        var changed = draft with { Confirmation = DutyConfirmation.Planned };
        Assert.Equal(8, changed.EffectiveEngineer(true));
        Assert.Null(changed.EffectiveTimeOff(true));
        Assert.Null(changed.Validate(true, true));
        Assert.Null((changed with { WantsTimeOff = false }).Validate(true, false));
        Assert.Contains("указать сотрудника", (draft with { ActualId = null }).Validate(true, false));
    }

    /// <summary>Перенос отгула удаляет интервал A и добавляет интервал B в одной транзакции.</summary>
    [Fact]
    public async Task EmployeeChangeTransfersOnlyMatchingOldInterval()
    {
        using var connection = new Probe();
        using var db = Db(connection);
        var result = await DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default);
        Assert.Equal(9, result.ActualId);
        Assert.Equal((byte)1, result.Closed);
        Assert.Equal(1, connection.Commits);
        Assert.Equal(8, connection.DeletedFor);
        Assert.Equal(9, connection.InsertedFor);
        Assert.Equal(new Interval(101, 9, NewDay), Assert.Single(connection.Intervals));
        Assert.Equal(NewDay.ToDateTime(TimeOnly.MinValue), connection.Row!.TimeOff);
    }

    /// <summary>При отказе одной из процедур не остаётся изменений ни в одной части сценария.</summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("insert")]
    public async Task ProcedureFailureRollsBackScheduleAndIntervals(string failing)
    {
        using var connection = new Probe { FailProcedure = failing };
        using var db = Db(connection);
        await Assert.ThrowsAsync<DutyRejectedException>(() => DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default));
        Assert.Equal(0, connection.Commits);
        Assert.Equal(1, connection.Rollbacks);
        Assert.Equal(Original, connection.Row);
        Assert.Equal(new Interval(100, 8, OldDay), Assert.Single(connection.Intervals));
    }

    /// <summary>Неоднозначная связь интервала со старым дежурством блокирует изменение.</summary>
    [Fact]
    public async Task MultipleOriginalIntervalsArePreserved()
    {
        using var connection = new Probe();
        connection.Intervals.Add(new Interval(200, 8, OldDay));
        using var db = Db(connection);
        await Assert.ThrowsAsync<DutyRejectedException>(() => DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default));
        Assert.Null(connection.DeletedFor);
        Assert.Null(connection.InsertedFor);
        Assert.Equal(2, connection.Intervals.Count);
        Assert.Equal(Original, connection.Row);
    }

    /// <summary>Закрытое, ещё не прошедшее и конкурентно изменённое дежурство отвергают прямой вызов.</summary>
    [Theory]
    [InlineData("closed")]
    [InlineData("today")]
    [InlineData("changed")]
    [InlineData("rights")]
    [InlineData("duplicate")]
    public async Task ServerRevalidatesStateUnderLock(string reason)
    {
        using var connection = new Probe();
        DutyRow? original = Original;
        var draft = ClosingDraft();
        if (reason == "closed") connection.Row = original = Original with { Closed = 1 };
        if (reason == "today")
        {
            connection.Row = original = Original with { DutyDate = Today };
            draft = draft with { Date = DateOnly.FromDateTime(Today) };
        }
        if (reason == "changed") connection.Row = Original with { ChangedAt = Original.ChangedAt.AddSeconds(1) };
        if (reason == "rights") connection.CanWrite = false;
        if (reason == "duplicate")
        {
            original = null;
            draft = new DutyDraft(DutyDate, Original.PlannedId);
        }
        using var db = Db(connection);
        var rejected = await Assert.ThrowsAsync<DutyRejectedException>(() =>
            DutyData.SaveAsync(db, "fixture", 4, original, draft, default));
        Assert.Equal(reason switch
        {
            "closed" => DutyRejectionKind.Closed,
            "changed" => DutyRejectionKind.Stale,
            "rights" => DutyRejectionKind.AccessDenied,
            "duplicate" => DutyRejectionKind.DateOccupied,
            _ => DutyRejectionKind.Validation,
        }, rejected.Kind);
        Assert.Equal(0, connection.Commits);
        Assert.Equal(0, connection.ScheduleWrites);
        Assert.Null(connection.InsertedFor);
        Assert.Null(connection.DeletedFor);
    }

    /// <summary>Незакрытый факт сохраняет дату, а рабочий день эффективного инженера исключает отгул.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftFactAndEffectiveCalendarUseCorrectEmployee(bool workDay)
    {
        using var connection = new Probe { WorkDay = workDay };
        using var db = Db(connection);
        var draft = ClosingDraft() with { Close = false, Confirmation = DutyConfirmation.Planned };
        var result = await DutyData.SaveAsync(db, "fixture", 4, Original, draft, default);
        Assert.Equal(8, connection.CalendarFor);
        Assert.Equal((byte)0, result.Closed);
        Assert.Equal(workDay ? (DateTime?)null : NewDay.ToDateTime(TimeOnly.MinValue), result.TimeOff);
        Assert.Null(connection.InsertedFor);
    }

    /// <summary>Плановая вставка и следующее редактирование используют разные SQL-команды.</summary>
    [Fact]
    public async Task PlanInsertAndUpdateAvoidFactAndAbsenceCommands()
    {
        using var connection = new Probe { Row = null };
        using var db = Db(connection);
        var draft = new DutyDraft(DateOnly.FromDateTime(Today), 8);
        var inserted = await DutyData.SaveAsync(db, "fixture", 4, null, draft, default);
        var updated = await DutyData.SaveAsync(db, "fixture", 4, inserted, draft with { PlannedId = 9 }, default);
        Assert.Equal(2, connection.Commits);
        Assert.Equal(9, updated.PlannedId);
        Assert.Equal(new[] { "insert", "plan" }, connection.Branches);
        Assert.Null(connection.DeletedFor);
        Assert.Null(connection.InsertedFor);
    }

    /// <summary>Потерянный ответ COMMIT не выдаётся за доказанный отказ и не повторяется.</summary>
    [Fact]
    public async Task LostCommitResponseKeepsBusinessOutcomeUnknown()
    {
        using var connection = new Probe { LoseCommitResponse = true };
        using var db = Db(connection);
        await Assert.ThrowsAsync<TimeoutException>(() => DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default));
        Assert.Equal(1, connection.Commits);
        Assert.Equal(0, connection.Rollbacks);
        Assert.Equal(1, connection.ScheduleWrites);
        Assert.Single(connection.Intervals);
    }

    /// <summary>Страница создаёт черновик с текущим днём и блокирует кнопку при недоступной записи.</summary>
    [Fact]
    public async Task NewReadOnlyEditorDisplaysDefaultWithoutWriting()
    {
        using var connection = new Probe { Row = null, CanWrite = false };
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("duty-test", false, 0, null, null));
        context.Services.AddScoped(_ => Db(connection));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<AuthenticationStateProvider>(new DutyAuthentication(true));
        context.Services.AddMudServices();
        context.Render<MudBlazor.MudPopoverProvider>();
        var page = context.Render<DutyPage>();
        page.WaitForAssertion(() => Assert.Contains("Редактирование недоступно", page.Markup));
        Assert.Contains("25.11.2026", page.Markup);
        Assert.True(page.FindAll("button").Single(button => button.TextContent.Trim() == "Сохранить").HasAttribute("disabled"));
        Assert.Equal(0, connection.ScheduleWrites);
    }

    /// <summary>Локальный снимок остаётся доступным при закрытой, исчезнувшей и недоступной для записи карточке.</summary>
    [Theory]
    [InlineData("closed")]
    [InlineData("missing")]
    [InlineData("read-only")]
    [InlineData("denied-read")]
    public async Task ReadOnlyRouteDisplaysOwnDraftWithoutDml(string mode)
    {
        using var connection = new Probe
        {
            Row = mode == "missing" ? null : Original with { Closed = (byte)(mode == "closed" ? 1 : 0), ActualId = 10, ActualName = "Инженер из БД" },
            CanWrite = mode != "read-only",
            DenyRead = mode == "denied-read",
        };
        var snapshot = JsonSerializer.Serialize(new { Original, Draft = ClosingDraft() });
        await using var context = EditorContext(connection,
            new ClayDraftRestore("duty-recovery", true, 9, null, null, new Dictionary<string, string> { ["main"] = snapshot }),
            rawReady: true);
        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, "20261121"));

        editor.WaitForAssertion(() => Assert.Contains("Локальный черновик", editor.Markup));
        Assert.Contains("29.11.2026", editor.Markup);
        Assert.Contains("#9", editor.Markup);
        Assert.DoesNotContain(editor.FindAll("button"),
            button => button.TextContent.Trim() == "Сохранить" && !button.HasAttribute("disabled"));
        Assert.Equal(0, connection.ScheduleWrites);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        var recovered = editor.WaitForElement("[aria-label='Восстановленный ввод']");
        var fields = editor.WaitForElements("[aria-label='Восстановленный ввод'] input[data-draft-key][readonly]", 4);
        string[] stableKeys = ["duty.actual", "duty.date", "duty.planned", "duty.time-off"];
        Assert.Equal(stableKeys, fields.Select(field => field.GetAttribute("data-draft-key")).Order(StringComparer.Ordinal));
        Assert.All(fields, field => Assert.False(field.HasAttribute("disabled")));
        foreach (var key in stableKeys)
            Assert.Single(editor.FindAll($"input[data-draft-key='{key}']"));
        editor.WaitForAssertion(() => Assert.Equal("main",
            Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]).Arguments[1]));
        var registration = Assert.Single(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Equal(new object[] { "duty", "20261121", "4" }, registration.Arguments.Take(3));
        var root = editor.Find(Assert.IsType<string>(registration.Arguments[4]));
        Assert.True(root.Contains(recovered));
        if (mode == "closed")
        {
            var result = editor.Find("[aria-label='Фактическая информация о дежурстве']");
            Assert.False(root.Contains(result));
            Assert.Contains("Инженер из БД", result.TextContent);
            Assert.DoesNotContain("Замечаний к дежурству нет", result.TextContent);
        }
        if (mode == "denied-read")
        {
            Assert.Contains("нет права прочитать актуальное дежурство", editor.Markup);
            Assert.DoesNotContain("Инженер из БД", editor.Markup);
        }

        await context.DisposeComponentsAsync();
        Assert.Equal("duty-recovery", Assert.Single(context.JSInterop.Invocations["clayDraftStore.unregister"]).Arguments[0]);
    }

    /// <summary>Закрытая карточка освобождает даже регистрацию, ответ которой пришёл после смены маршрута.</summary>
    [Fact]
    public async Task ReadOnlyRegistrationIsReleasedWhenRouteChangesBeforeItReturns()
    {
        using var connection = new Probe { Row = Original with { Closed = 1 } };
        var restored = new ClayDraftRestore("old-duty", true, 9, null, null);
        await using var context = EditorContext(connection, restored, deferRegistration: true);
        var registration = context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register",
            call => Equals(call.Arguments[1], "20261121"));
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.JSInterop.SetupVoid("clayDraftStore.unregister", call =>
        {
            if (!Equals(call.Arguments[0], "old-duty")) return false;
            released.TrySetResult();
            return true;
        }).SetVoidResult();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/Duty/duty_form?id=20261121");
        var page = context.Render<DutyPage>();
        page.WaitForAssertion(() => Assert.Single(registration.Invocations));

        try
        {
            await page.InvokeAsync(() => navigation.NavigateTo("/Duty/duty_form?id=invalid"));
            page.WaitForAssertion(() => Assert.Contains("Некорректная дата", page.Markup));
        }
        finally { registration.SetResult(restored); }
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal(0, connection.ScheduleWrites);
    }

    /// <summary>Даже без typed-снимка запись остаётся доступна для восстановления сырых полей.</summary>
    [Fact]
    public async Task MissingDutyWithRawOnlyDraftRendersRecoveryTargets()
    {
        using var connection = new Probe { Row = null };
        await using var context = EditorContext(connection,
            new ClayDraftRestore("duty-raw-recovery", true, 9, null, null), rawReady: true);
        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, "20261121"));

        editor.WaitForElement("[aria-label='Восстановленный ввод'] input[data-draft-key='duty.time-off'][readonly]");
        editor.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        Assert.DoesNotContain("Локальный черновик", editor.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, connection.ScheduleWrites);
    }

    /// <summary>Сверка читает дату заново без замены официальной карточки, прав записи или локального ввода.</summary>
    [Theory]
    [InlineData("closed")]
    [InlineData("missing")]
    [InlineData("denied-read")]
    [InlineData("read-only")]
    public async Task ReadOnlyRecoveryFreshComparisonRemainsSeparate(string mode)
    {
        using var connection = new Probe
        {
            Row = mode == "missing" ? null : Original with
            {
                Closed = (byte)(mode == "closed" ? 1 : 0), PlannedName = "Исходный инженер",
            },
            CanWrite = mode != "read-only",
            DenyRead = mode == "denied-read",
        };
        var snapshot = JsonSerializer.Serialize(new { Original, Draft = ClosingDraft() });
        await using var context = EditorContext(connection,
            new ClayDraftRestore("duty-recovery", true, 9, null, null, new Dictionary<string, string> { ["main"] = snapshot }),
            rawReady: true);
        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, "20261121"));
        editor.WaitForElement("[aria-label='Восстановленный ввод']");
        editor.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        var originalClosed = editor.FindAll("[aria-label='Фактическая информация о дежурстве']").SingleOrDefault()?.TextContent;

        connection.DenyRead = false;
        connection.CanWrite = true;
        connection.Row = Original with { Closed = 0, PlannedName = "Инженер из свежего чтения" };
        await editor.FindAll("button").Single(button => button.TextContent.Trim() == "Сверить состояние записи")
            .ClickAsync(new MouseEventArgs());

        var comparison = editor.Find("[aria-label='Актуальные данные для сверки']");
        Assert.Contains("Инженер из свежего чтения", comparison.TextContent);
        Assert.Equal(originalClosed, editor.FindAll("[aria-label='Фактическая информация о дежурстве']").SingleOrDefault()?.TextContent);
        Assert.NotNull(editor.Find("[aria-label='Локальный черновик']"));
        Assert.Equal(4, editor.FindAll("input[data-draft-key^='duty.']").Count);
        Assert.DoesNotContain(editor.FindAll("button"), button =>
            button.TextContent.Trim() == "Сохранить" && !button.HasAttribute("disabled"));

        connection.DenyRead = true;
        await editor.FindAll("button").Single(button => button.TextContent.Trim() == "Сверить состояние записи")
            .ClickAsync(new MouseEventArgs());
        Assert.Empty(editor.FindAll("[aria-label='Актуальные данные для сверки']"));
        Assert.Contains("Не удалось прочитать актуальную запись для сверки", editor.Markup);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, connection.ScheduleWrites);
    }

    /// <summary>Восстановленный Unknown показывает отправленный снимок и разрешает только чтение.</summary>
    [Fact]
    public async Task ClosedDutyRestoresAttemptForReadOnlyComparison()
    {
        using var connection = new Probe { Row = Original with { Closed = 1 } };
        var sent = JsonSerializer.Serialize(new { Original, Draft = ClosingDraft() });
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "duty-attempt-01", kind = "duty.save", status = "unknown", revision = 9,
            data = sent, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = EditorContext(connection, new ClayDraftRestore("duty-recovery", true, 9, null, operation));
        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, "20261121"));

        editor.WaitForAssertion(() => Assert.Contains("duty-attempt-01", editor.Markup));
        Assert.Contains("29.11.2026", editor.Markup);
        Assert.DoesNotContain("Отбросить черновик", editor.Markup);
        var compare = editor.FindAll("button").Single(button => button.TextContent.Trim() == "Сверить состояние записи");
        await compare.ClickAsync(new MouseEventArgs());

        Assert.Contains("проверка журнала", editor.Markup);
        Assert.Equal(0, connection.ScheduleWrites);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
    }

    /// <summary>В закрытой карточке без черновика отсутствуют и сохранение, и durable snapshot.</summary>
    [Fact]
    public async Task CleanClosedViewDoesNotPersistDraft()
    {
        using var connection = new Probe { Row = Original with { Closed = 1 } };
        await using var context = EditorContext(connection, new ClayDraftRestore("duty-clean", false, 0, null, null));
        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, "20261121"));

        editor.WaitForAssertion(() => Assert.Contains("Закрыто", editor.Markup));
        Assert.DoesNotContain("Локальный черновик", editor.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Equal(0, connection.ScheduleWrites);
    }

    /// <summary>Поиск черновика требует валидного ключа и подтверждённого текущего сотрудника.</summary>
    [Theory]
    [InlineData("20261121", false)]
    [InlineData("invalid", true)]
    public async Task UntrustedRouteOrIdentityDoesNotOpenStoredDraft(string route, bool authenticated)
    {
        using var connection = new Probe();
        await using var context = EditorContext(connection, new ClayDraftRestore("private", true, 9, null, null), authenticated);
        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, route));
        editor.WaitForAssertion(() => Assert.Contains(authenticated ? "Некорректная дата" : "Не удалось подтвердить", editor.Markup));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Equal(0, connection.ScheduleWrites);
    }

    /// <summary>Закрытый итог показывает один из трёх результатов, факультативного работодателя и нулевую оплату.</summary>
    [Theory]
    [InlineData(null, 12, "Никто не дежурил")]
    [InlineData(8, 12, "Замечаний к дежурству нет")]
    [InlineData(9, null, "Инженер замены")]
    [InlineData(9, 12, "Инженер замены")]
    public async Task ClosedResultKeepsLegacyMeaning(int? actual, int? employer, string expected)
    {
        using var connection = new Probe
        {
            Row = Original with { Closed = 1, ActualId = actual, ActualName = "Инженер замены", EmployerId = employer, Payment = 0 },
        };
        await using var context = EditorContext(connection, new ClayDraftRestore("no-draft", false, 0, null, null));
        var page = context.Render<DutyEditor>(parameters => parameters.Add(editor => editor.RouteId, "20261121"));

        page.WaitForAssertion(() => Assert.Contains(expected, page.Markup));
        var result = page.Find("[aria-label='Фактическая информация о дежурстве']");
        Assert.Equal(actual == 9 && employer is not null, result.TextContent.Contains("Работодатель:", StringComparison.Ordinal));
        Assert.Equal(actual is not null, result.TextContent.Contains("Оплата: 0,00", StringComparison.Ordinal));
        Assert.Equal(actual is not null, result.TextContent.Contains("Дата отгула: 28.11.2026", StringComparison.Ordinal));
        if (actual is null) Assert.NotNull(result.QuerySelector(".mud-error-text"));
        if (actual == 9) Assert.Contains("Дежурный инженер:", result.TextContent);
        Assert.Equal(0, connection.ScheduleWrites);
    }

    /// <summary>Смена версии или календарного дня блокирует старый Save и оставляет доступным чтение.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalePlanOrMidnightEntersReadOnlyComparison(bool midnight)
    {
        var plan = Original with { DutyDate = Today, ActualId = null, TimeOff = null };
        using var connection = new Probe { Row = plan };
        await using var context = EditorContext(connection,
            new ClayDraftRestore("conflict-test", false, 0, null, null), rawReady: true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"duty-conflict","kind":"duty.save","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, "20261125"));
        editor.WaitForAssertion(() => Assert.False(editor.FindAll("button").Single(b => b.TextContent.Trim() == "Сохранить").HasAttribute("disabled")));
        if (midnight) connection.ServerToday = Today.AddDays(1);
        else connection.Row = plan with { PlannedId = 17, ChangedAt = plan.ChangedAt.AddHours(1) };

        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Сохранить").ClickAsync(new MouseEventArgs());

        Assert.Contains(midnight ? "Серверный день изменился" : "Дежурство изменилось", editor.Markup);
        Assert.Equal(0, connection.ScheduleWrites);
        Assert.True(editor.FindAll("button").Single(b => b.TextContent.Trim() == "Сохранить").HasAttribute("disabled"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Сверить состояние записи").ClickAsync(new MouseEventArgs());

        Assert.Contains(midnight ? "26.11.2026" : "#17", editor.Markup);
        Assert.Contains("Локальный черновик", editor.Markup);
        Assert.Equal(0, connection.ScheduleWrites);
        Assert.True(editor.FindAll("button").Single(b => b.TextContent.Trim() == "Сохранить").HasAttribute("disabled"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Подтверждённый конфликт плана можно сверить и принять свежую исходную версию без повторной записи.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task StalePlanCanExplicitlyKeepLocalEngineerWithoutWriting(bool accepted, bool hasRaw, bool changedAgain)
    {
        var plan = Original with { DutyDate = Today, ActualId = null, TimeOff = null };
        using var connection = new Probe { Row = plan };
        await using var context = EditorContext(connection,
            new ClayDraftRestore("cas-plan", false, 0, null, null), rawReady: true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"duty-conflict","kind":"duty.save","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(7, hasRaw ? ["data:duty.planned:0"] : []));
        context.JSInterop.Setup<bool>("clayDraftStore.rebaseScope", _ => true).SetResult(accepted);

        var editor = context.Render<DutyEditor>(parameters => parameters.Add(component => component.RouteId, "20261125"));
        editor.WaitForAssertion(() => Assert.False(Button("Сохранить").HasAttribute("disabled")));
        await editor.InvokeAsync(() => editor.FindComponent<ClaySelect<DutyEngineer, int?>>().Instance.ValueChanged.InvokeAsync(9));
        connection.Row = plan with { PlannedId = 17, ChangedAt = plan.ChangedAt.AddHours(1) };

        await Button("Сохранить").ClickAsync(new MouseEventArgs());
        await Button("Сверить состояние записи").ClickAsync(new MouseEventArgs());
        Assert.True(Button("Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        var comparison = editor.Find("[aria-label='Сравнение планового инженера']");
        Assert.Contains("#8", comparison.TextContent);
        Assert.Contains("#9", comparison.TextContent);
        Assert.Contains("#17", comparison.TextContent);

        if (hasRaw)
        {
            Assert.DoesNotContain(editor.FindAll("button"), button => button.TextContent.Contains("Оставить моего инженера"));
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        }
        else if (changedAgain)
        {
            connection.Row = plan with { PlannedId = 18, ChangedAt = plan.ChangedAt.AddHours(2) };
            await Button("Оставить моего инженера").ClickAsync(new MouseEventArgs());
            Assert.Contains("Данные или доступ изменились", editor.Markup);
            Assert.True(Button("Сохранить").HasAttribute("disabled"));
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        }
        else
        {
            await Button("Оставить моего инженера").ClickAsync(new MouseEventArgs());
            var rebase = Assert.Single(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
            Assert.Equal(7L, rebase.Arguments[2]);
            using var saved = JsonDocument.Parse(Assert.IsType<string>(rebase.Arguments[3]));
            Assert.Equal(17, saved.RootElement.GetProperty("Original").GetProperty("PlannedId").GetInt32());
            Assert.Equal(9, saved.RootElement.GetProperty("Draft").GetProperty("PlannedId").GetInt32());
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(rebase.Arguments[4]));
            Assert.Equal(!accepted, Button("Сохранить").HasAttribute("disabled"));
        }

        Assert.Equal(0, connection.ScheduleWrites);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        AngleSharp.Dom.IElement Button(string text) => editor.FindAll("button").Single(b => b.TextContent.Trim() == text);
    }

    /// <summary>Смена только отображаемых имён не является изменением версии строки.</summary>
    [Fact]
    public async Task DisplayNameChangesDoNotRejectMatchingBusinessVersion()
    {
        using var connection = new Probe
        {
            Row = Original with { PlannedName = "Новое имя", ActualName = "Новое имя", ChangedByName = "Уточнённое имя" },
        };
        using var db = Db(connection);

        var result = await DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default);

        Assert.Equal((byte)1, result.Closed);
        Assert.Equal(1, connection.ScheduleWrites);
        Assert.Equal(1, connection.Commits);
    }

    private static BunitContext EditorContext(Probe connection, ClayDraftRestore restore, bool authenticated = true, bool rawReady = false, bool deferRegistration = false,
        NavigationManager? navigation = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        if (!deferRegistration)
            context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true).SetResult(restore);
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult(rawReady ? ["data:duty.time-off:0"] : [], rawReady ? [] : ["data:duty.time-off:0"]));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(rawReady, rawReady ? [] : ["main"], []));
        context.Services.AddScoped(_ => Db(connection));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<AuthenticationStateProvider>(new DutyAuthentication(authenticated));
        if (navigation is not null) context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddMudServices();
        context.Render<MudBlazor.MudPopoverProvider>();
        return context;
    }

    private sealed class DutyAuthentication(bool authenticated) : AuthenticationStateProvider
    {
        internal Func<Task>? BeforeRead { get; set; }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            if (BeforeRead is not null) await BeforeRead();
            var identity = authenticated ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test") : new ClaimsIdentity();
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
    }

    private static DutyDraft ClosingDraft() => new(DutyDate, 8, DutyConfirmation.Other, 9, true, NewDay, true);

    private static DbManager Db(Probe connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
    }

    private sealed record Interval(int Id, int Employee, DateOnly Date);

    private sealed class Probe : DbConnection
    {
        public DutyRow? Row { get; set; } = Original;
        public List<Interval> Intervals { get; set; } = [new(100, 8, OldDay)];
        public bool CanWrite { get; set; } = true;
        public bool DenyRead { get; set; }
        public int CurrentEmployeeId { get; set; } = 4;
        public bool CalendarReadFails { get; set; }
        public DateTime ServerToday { get; set; } = Today;
        public bool WorkDay { get; init; }
        public string? FailProcedure { get; init; }
        public bool LoseCommitResponse { get; init; }
        public int Commits { get; set; }
        public int Rollbacks { get; set; }
        public int? DeletedFor { get; set; }
        public int? InsertedFor { get; set; }
        public int? CalendarFor { get; set; }
        public int ScheduleWrites { get; set; }
        public List<string> Branches { get; } = [];

        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            Assert.Equal(IsolationLevel.Serializable, level);
            return new ProbeTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new ProbeCommand(this);
    }

    private sealed class ProbeTransaction(Probe connection) : DbTransaction
    {
        public DutyRow? Row = connection.Row;
        public List<Interval> Intervals = [.. connection.Intervals];
        private bool _finished;
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => connection;
        public override void Commit()
        {
            Assert.False(_finished);
            connection.Row = Row;
            connection.Intervals = Intervals;
            connection.Commits++;
            _finished = true;
            if (connection.LoseCommitResponse) throw new TimeoutException();
        }
        public override void Rollback()
        {
            if (!_finished) connection.Rollbacks++;
            _finished = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Rollback();
            base.Dispose(disposing);
        }
    }

    private sealed class ProbeCommand(Probe owner) : DbCommand
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

        private T Param<T>(string key) => (T)Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == key).Value!;
        private DateOnly Day(string key) => DateOnly.FromDateTime(Param<DateTime>(key));
        private ProbeTransaction Transaction => Assert.IsType<ProbeTransaction>(DbTransaction);

        public override int ExecuteNonQuery()
        {
            var tx = Transaction;
            if (CommandText.Contains("sp_НерабочиеДниDel"))
            {
                var old = tx.Intervals.Single(row => row.Id == Param<int>("intervalId"));
                owner.DeletedFor = old.Employee;
                if (owner.FailProcedure == "delete") throw new InvalidOperationException("fixture deletion error");
                tx.Intervals.Remove(old);
            }
            else if (CommandText.Contains("sp_НерабочиеДниIns"))
            {
                owner.InsertedFor = Param<int>("employeeId");
                if (owner.FailProcedure == "insert") throw new InvalidOperationException("fixture insertion error");
                tx.Intervals.Add(new Interval(101, owner.InsertedFor.Value, Day("timeOff")));
            }
            else
            {
                owner.ScheduleWrites++;
                if (CommandText.StartsWith("INSERT", StringComparison.Ordinal))
                {
                    owner.Branches.Add("insert");
                    tx.Row = Original with { DutyDate = Param<DateTime>("date"), PlannedId = Param<int>("engineerId"), ActualId = null, TimeOff = null };
                }
                else if (CommandText.Contains("SET КодСотрудника ="))
                {
                    owner.Branches.Add("plan");
                    tx.Row = tx.Row! with { PlannedId = Param<int>("engineerId") };
                }
                else
                {
                    owner.Branches.Add("fact");
                    tx.Row = tx.Row! with { ActualId = Param<int>("actualId"), Closed = Param<byte>("close"),
                        TimeOff = Param<object>("timeOff") is DateTime off ? off : null, WorkDay = (byte)(owner.WorkDay ? 1 : 0) };
                }
            }
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("SUSER_SID()")) return Values(("КодСотрудника", typeof(int), owner.CurrentEmployeeId));
            if (CommandText.Contains("WITH Schedule AS")) return Values(("Today", typeof(DateTime), owner.ServerToday));
            if (CommandText.Contains("CanChangeTimeOff"))
                return Values(("Today", typeof(DateTime), owner.ServerToday), ("CanInsert", typeof(bool), owner.CanWrite),
                    ("CanUpdate", typeof(bool), owner.CanWrite), ("CanChangeTimeOff", typeof(bool), owner.CanWrite));
            if (CommandText.Contains("parentLocation"))
            {
                if (owner.CalendarReadFails) throw new InvalidOperationException("fixture calendar failure");
                owner.CalendarFor = Param<int>("employeeId");
                return Values(("Value", typeof(bool), owner.WorkDay));
            }
            if (CommandText.Contains("tally.Total"))
                return Values(("Id", typeof(int), Param<int>("id")), ("Name", typeof(string), "Инженер"),
                    ("Total", typeof(int), null), ("Weekends", typeof(int), null));
            if (CommandText.Contains("ЗаявкиIT.dbo.НерабочиеДни"))
            {
                var employee = Param<int>("employeeId");
                var day = Day("timeOff");
                var table = new DataTable();
                table.Columns.Add("Id", typeof(int));
                foreach (var interval in Transaction.Intervals.Where(row => row.Employee == employee && row.Date == day))
                    table.Rows.Add(interval.Id);
                return table.CreateDataReader();
            }
            if (CommandText.Contains("Дата <> @date")) return Values(("Value", typeof(bool), false));
            if (DbTransaction is not null) Assert.Contains("UPDLOCK", CommandText);
            if (owner.DenyRead && DbTransaction is null)
                throw SqlExceptionFactory.Create(229, "Тестовый отказ SELECT");
            var source = DbTransaction is ProbeTransaction transaction ? transaction.Row : owner.Row;
            if (source is not { } row)
            {
                var empty = new DataTable();
                empty.Columns.Add("DutyDate", typeof(DateTime));
                return empty.CreateDataReader();
            }
            return Values(("DutyDate", typeof(DateTime), row.DutyDate), ("PlannedId", typeof(int), row.PlannedId),
                ("ActualId", typeof(int), row.ActualId), ("EmployerId", typeof(int), row.EmployerId),
                ("WorkDay", typeof(byte), row.WorkDay), ("Payment", typeof(decimal), row.Payment),
                ("TimeOff", typeof(DateTime), row.TimeOff), ("Closed", typeof(byte), row.Closed),
                ("ChangedById", typeof(int), row.ChangedById), ("ChangedAt", typeof(DateTime), row.ChangedAt),
                ("PlannedName", typeof(string), row.PlannedName), ("ActualName", typeof(string), row.ActualName),
                ("ChangedByName", typeof(string), row.ChangedByName));
        }

        private static DbDataReader Values(params (string Name, Type Type, object? Value)[] values)
        {
            var table = new DataTable();
            foreach (var (name, type, _) in values) table.Columns.Add(name, type);
            table.Rows.Add(values.Select(item => item.Value ?? DBNull.Value).ToArray());
            return table.CreateDataReader();
        }
    }
}

