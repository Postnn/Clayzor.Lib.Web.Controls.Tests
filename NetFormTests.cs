using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Locations;
using Clayzor.Lib.Web.DBSelect.Phones;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NetPage = Kesco.App.Web.Inventory.Components.Pages.Net.Net;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Сквозные проверки формы порта локальной сети.</summary>
public sealed partial class NetFormTests
{
    /// <summary>R02/R12/R14: ссылки независимы, не меняют текст и открывают нужное оборудование через clayOpen.</summary>
    [Fact]
    public async Task Load_LinksStayIndependentAndOpenEquipmentWithoutNavigation()
    {
        var fixture = new NetFixture(
            connected: "missing server01",
            description: "server01",
            linkEntry: Script.Rows(Script.Row(("Token", "server01"), ("КодОборудования", 8))));
        await using var context = fixture.Context;
        var page = context.Render<NetPage>();
        WaitForLoad(page);

        var model = Model(page.Instance);
        Assert.Equal("missing server01", model.Connected);
        Assert.Equal("server01", model.Description);
        Assert.Equal("clay-test-net_2", page.Find("[data-testid='net-port-caption']").TextContent);
        var connected = page.Find("[data-testid='net-connected-links']");
        var description = page.Find("[data-testid='net-description-links']");
        Assert.Equal("server01", connected.TextContent.Trim());
        Assert.Equal("server01", description.TextContent.Trim());
        Assert.DoesNotContain("missing", connected.TextContent);
        await connected.QuerySelector("a")!.ClickAsync();

        var invocation = context.JSInterop.Invocations.Single(call => call.Identifier == "clayFormWindow.open");
        Assert.Equal("http://localhost/equipment?id=8", invocation.Arguments[0]);
        Assert.Equal("equipment-form", invocation.Arguments[1]);
        Assert.Equal("http://localhost/Net/Net?id=10", fixture.Navigation.Uri);
    }

    /// <summary>Подвал Changed показывает загруженное имя сотрудника, а не текст Razor-привязки.</summary>
    [Fact]
    public void ChangedFooter_UsesLoadedEmployeeName()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        var changed = page.Find(".clay-changed");
        Assert.Contains("Тестов А.", changed.TextContent);
        Assert.DoesNotContain("_model?.ChangedByName", changed.TextContent);
    }

    /// <summary>Форма использует всю доступную ширину окна, как EquipmentForm и Tel.</summary>
    [Fact]
    public void EntityForm_UsesFullAvailableWidth()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        Assert.Contains("max-width:100%", page.Find(".clay-entity-form").GetAttribute("style"));
        Assert.Contains("max-width:100%", page.Find(".clay-entity-form-footer").GetAttribute("style"));
        Assert.Single(page.FindAll(".mud-grid-item").Where(item => item.ClassList.Contains("equipment-ins-fields")));
    }

    /// <summary>Некорректный ID показывает отсутствие записи без обращения к БД.</summary>
    [Fact]
    public void InvalidId_ShowsNotFoundWithoutSql()
    {
        using var fixture = new NetFixture(id: 0);
        var page = fixture.Context.Render<NetPage>();
        page.WaitForAssertion(() => Assert.Contains("не найден", page.Markup));
        Assert.Empty(fixture.Connection.CommandLog);
    }

    /// <summary>Ошибка resolver не удаляет исходный текст и не блокирует саму форму.</summary>
    [Fact]
    public void LinkResolverError_PreservesLoadedText()
    {
        using var fixture = new NetFixture(connected: "server01",
            linkEntry: new ScriptEntry { Exception = SqlExceptionFactory.Create(53, "connection error") });
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Assert.Equal("server01", Model(page.Instance).Connected);
        Assert.Contains("Не удалось сформировать ссылки", page.Markup);
        Assert.Empty(page.FindAll("[data-testid='net-connected-links']"));
    }

    /// <summary>Розетка без сотрудника и подключённого текста отклоняется до нового SQL.</summary>
    [Fact]
    public async Task SocketValidation_DoesNotWrite()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var count = fixture.Connection.CommandLog.Count;
        Model(page.Instance).SocketId = 5;

        await Invoke(page, "SaveAsync", false);

        Assert.Contains("Вы указали розетку", page.Markup);
        Assert.Equal(count, fixture.Connection.CommandLog.Count);
    }

    /// <summary>R18: Apply записывает, остаётся на форме и строит ссылки из новой сохранённой записи.</summary>
    [Fact]
    public async Task Apply_ReloadsSavedTextAndLinks()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.AddSaveDbCommand(Script.Scalar(0));
        fixture.AddLoad(connected: "srv-new",
            linkEntry: Script.Rows(Script.Row(("Token", "srv-new"), ("КодОборудования", 8))));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Connected = "srv-new";

        await Invoke(page, "SaveAsync", false);

        page.WaitForAssertion(() => Assert.Equal("srv-new", Model(page.Instance).Connected));
        page.WaitForAssertion(() => Assert.Equal("srv-new",
            page.Find("[data-testid='net-connected-links']").TextContent.Trim()));
        Assert.Contains(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть"));
        Assert.Contains(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>OK без изменений закрывает форму без UPDATE.</summary>
    [Fact]
    public async Task OkWithoutChanges_ClosesWithoutSql()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var count = fixture.Connection.CommandLog.Count;

        await Invoke(page, "SaveAsync", true);

        Assert.Equal(count, fixture.Connection.CommandLog.Count);
        Assert.Contains(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Сотрудник подставляет организацию без очистки заказчика при сбросе, розетка управляет своим примечанием.</summary>
    [Fact]
    public async Task EmployeeAndSocketChanges_PreserveLegacyDependencies()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 2))));
        fixture.Enqueue(Script.Rows(Script.Row(("КодРозетки", 5), ("Розетка", "A-1"),
            ("Примечание", "socket note"), ("КодРасположения", 55), ("Работает", true))));
        fixture.Enqueue(Script.Rows(Script.Row(("КодЛица", 2), ("Кличка", "Организация 2"))));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        await Invoke(page, "OnEmployeeChangedAsync", 4);
        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
        await Invoke(page, "OnEmployeeChangedAsync", (int?)null);
        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
        await Invoke(page, "OnSocketChangedAsync", 5);
        Assert.Equal("socket note", GetPrivate<string>(page.Instance, "_socketNotes"));
        await Invoke(page, "OnSocketChangedAsync", (int?)null);
        Assert.Null(GetPrivate<string>(page.Instance, "_socketNotes"));
        Render(page);
        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
    }

    /// <summary>Выбор переключения/обмена сбрасывает альтернативу и скрывает её строку.</summary>
    [Fact]
    public async Task PortModes_AreMutuallyExclusive()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        await Invoke(page, "OnSwitchChanged", 11);
        Render(page);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-testid='net-exchange-row']")));
        Assert.Equal(-1, GetPrivate<int?>(page.Instance, "_exchangeWithId"));
        await Invoke(page, "OnExchangeChanged", 12);
        Render(page);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-testid='net-switch-row']")));
        Assert.Equal(-1, GetPrivate<int?>(page.Instance, "_switchToId"));
    }

    /// <summary>Выключение срока возврата очищает дату; просроченная дата остаётся предупреждением.</summary>
    [Fact]
    public async Task ReturnDateCheckbox_ClearsDraftAndShowsLegacyWarning()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).ReturnDate = DateTime.Today.AddDays(-1);

        await Invoke(page, "OnReturnDateEnabledChanged", true);
        Render(page);
        page.WaitForAssertion(() => Assert.Contains("просрочено", page.Markup));
        await Invoke(page, "OnReturnDateEnabledChanged", false);
        Render(page);

        Assert.Null(Model(page.Instance).ReturnDate);
        Assert.Empty(page.FindAll(".mud-picker"));
    }

    /// <summary>Неизвестный или утративший идентичность пользователь не выполняет запись.</summary>
    [Fact]
    public async Task SaveWithoutCurrentIdentity_FailsClosed()
    {
        using var fixture = new NetFixture(currentUser: false);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Description = "draft";
        var count = fixture.Connection.CommandLog.Count;

        await Invoke(page, "SaveAsync", false);

        Assert.Equal(count, fixture.Connection.CommandLog.Count);
        Assert.Contains("Текущий пользователь не определён", page.Markup);
    }

    /// <summary>PoE требует подтверждение, использует stub с timeout 120 и снимает overlay.</summary>
    [Fact]
    public async Task Poe_ConfirmationUsesStubAndAlwaysEndsOverlay()
    {
        using var fixture = new NetFixture(poe: 1);
        fixture.AddPoeRestart(Script.NonQuery());
        var provider = fixture.Context.Render<MudDialogProvider>();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "RestartPoeAsync");
        provider.WaitForAssertion(() => Assert.Contains("Перезапустить PoE", provider.Markup));

        await page.InvokeAsync(() => provider.FindAll("button").Single(button => button.TextContent.Trim() == "Да").Click());
        await pending;

        var journalCalls = fixture.Context.JSInterop.Invocations.Select(call => call.Identifier)
            .Where(identifier => identifier is "clayDraftStore.recordConfirmedOperation" or "clayDraftStore.commitOperation");
        Assert.Equal(["clayDraftStore.recordConfirmedOperation", "clayDraftStore.commitOperation"], journalCalls);
        Assert.Equal(120, fixture.Connection.TimeoutLog.Single());
        Assert.Equal(InventoryOperationState.Confirmed, fixture.ServerJournal.PoeState);
        Assert.False(GetPrivate<bool>(page.Instance, "_poeRunning"));
        Assert.Empty(page.FindAll(".mud-alert").Where(alert => alert.TextContent.Contains("Ошибка при перезапуске PoE")));
        AssertNetBeginBlocksOtherDrafts(Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]));
    }

    /// <summary>Исчезновение текущей идентичности перед записью запрещает UPDATE.</summary>
    [Fact]
    public async Task IdentityLostAfterLoad_DoesNotWrite()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows());
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Description = "draft";

        await Invoke(page, "SaveAsync", false);

        Assert.Contains("Сохранение запрещено", page.Markup);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть"));
    }

    /// <summary>R18: ошибка записи сохраняет черновик и не закрывает форму.</summary>
    [Fact]
    public async Task SaveConnectivityError_PreservesDraftWithoutClose()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.AddSaveDbCommand(Script.Error(SqlExceptionFactory.Create(53, "connection error")));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Description = "unsaved draft";

        await Invoke(page, "SaveAsync", false);

        Assert.Equal("unsaved draft", Model(page.Instance).Description);
        Assert.Contains("Результат команды «Сохранить» неизвестен", page.Markup);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
        fixture.Enqueue(Script.Rows(Script.Row(("КодЛокальнойСети", 10), ("Изменено", new DateTime(2026, 11, 21, 11, 0, 0)),
            ("Примечания", "unsaved draft"))));
        fixture.AddPoeRead();
        await OperationJournalButton(page, "Прочитать состояние порта").ClickAsync();
        Assert.Contains("Чтение записи не подтверждает", page.Markup);
        var afterRead = fixture.Connection.CommandLog.Count;
        await Invoke(page, "SaveAsync", false);
        Assert.Equal(afterRead, fixture.Connection.CommandLog.Count);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Потеря ответа COMMIT очищает принятую ревизию только после точной серверной сверки.</summary>
    [Fact]
    public async Task Save_LostCommitReplyReconcilesFromServerWithoutAnotherWrite()
    {
        using var fixture = new NetFixture();
        fixture.ServerJournal.LoseNextConfirmedCommitReply = true;
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
        fixture.AddSaveDbCommand(Script.Scalar(0));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        await TypeText(page, 1, "отправлено");

        await Invoke(page, "SaveAsync", false);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        await TypeText(page, 1, "поздний ввод");
        fixture.AddPoeRead();
        await OperationJournalButton(page, "Прочитать состояние порта").ClickAsync();

        Assert.Equal(9L, Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
        Assert.Equal("поздний ввод", Model(page.Instance).Description);
        Assert.Contains("Сохранение подтверждено сервером", page.Markup);
        Assert.Single(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть"));
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Ошибка stub PoE показывает сообщение и всегда снимает overlay.</summary>
    [Fact]
    public async Task PoeStubError_ShowsErrorAndEndsOverlay()
    {
        using var fixture = new NetFixture(poe: 1);
        fixture.AddPoeRestart(Script.Error(SqlExceptionFactory.Create(50000, "stub error")));
        var provider = fixture.Context.Render<MudDialogProvider>();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "RestartPoeAsync");
        provider.WaitForAssertion(() => Assert.Contains("Перезапустить PoE", provider.Markup));

        await page.InvokeAsync(() => provider.FindAll("button").Single(button => button.TextContent.Trim() == "Да").Click());
        await pending;

        Assert.Contains("Результат команды «Перезапустить PoE» неизвестен", page.Markup);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Equal(InventoryOperationState.Dispatching, fixture.ServerJournal.PoeState);
        var count = fixture.Connection.CommandLog.Count;
        await Invoke(page, "RestartPoeAsync");
        await Invoke(page, "SaveAsync", false);
        Assert.Equal(count, fixture.Connection.CommandLog.Count);
        Assert.False(GetPrivate<bool>(page.Instance, "_poeRunning"));
    }

    /// <summary>Серверная попытка PoE блокирует запись и повтор на форме без локального маркера.</summary>
    [Fact]
    public async Task UnresolvedServerPoeBlocksWritesWithoutLocalAttempt()
    {
        using var fixture = new NetFixture(poe: 1);
        fixture.ServerJournal.SeedUnresolvedPoe(10);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        page.WaitForAssertion(() =>
        {
            Assert.Contains("В серверном журнале есть незавершённая операция с портом", page.Markup);
            Assert.True(NetReadinessButton(page, "ОК").HasAttribute("disabled"));
            Assert.True(NetReadinessButton(page, "Перезапустить PoE").HasAttribute("disabled"));
        });
        await Invoke(page, "SaveAsync", false);
        await Invoke(page, "RestartPoeAsync");

        Assert.DoesNotContain(fixture.Connection.CommandLog, sql =>
            sql.Contains("UPDATE vwЛокальнаяСеть") || sql.Contains("sp_ResetPoEport"));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Чтение принимает только точное серверное подтверждение PoE, без повторного вызова процедуры.</summary>
    [Theory]
    [InlineData(InventoryOperationState.Confirmed, 10, true)]
    [InlineData(InventoryOperationState.Confirmed, 99, false)]
    [InlineData(InventoryOperationState.Dispatching, null, false)]
    public async Task PoeOutcomeReadOnlyAcceptsConfirmedReceiptForThisPort(
        InventoryOperationState state, int? resultId, bool confirmed)
    {
        var attemptId = Guid.NewGuid();
        using var fixture = new NetFixture(poe: 1, restoredOperation: OperationJson("net.poe", "unknown", attemptId.ToString("N")));
        fixture.ServerJournal.SeedPoe(attemptId, 10, state, resultId);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        fixture.AddPoeRead();
        if (!confirmed) fixture.AddPoeRead();

        await page.InvokeAsync(() => OperationJournalButton(page, "Прочитать состояние порта").ClickAsync());

        var commits = fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        if (confirmed)
        {
            Assert.Equal(9L, Assert.Single(commits).Arguments[1]);
            Assert.Contains("Сервер подтвердил успешный ответ на команду PoE", page.Markup);
        }
        else
        {
            Assert.Empty(commits);
            Assert.True(NetReadinessButton(page, "Перезапустить PoE").HasAttribute("disabled"));
        }
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("sp_ResetPoEport"));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>R19: поздний resolver после dispose не записывает ссылки в закрытую форму.</summary>
    [Fact]
    public async Task LateResolverAfterDispose_DoesNotApplyLinks()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new NetFixture(connected: "server01", linkEntry: new ScriptEntry
        {
            Started = started,
            Release = release,
            Rows = [Script.Row(("Token", "server01"), ("КодОборудования", 8))],
        });
        var page = fixture.Context.Render<NetPage>();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await page.Instance.DisposeAsync();
        release.SetResult();
        await Task.Delay(100);

        Assert.Empty(page.FindAll("[data-testid='net-connected-links']"));
    }

    /// <summary>Первичная SQL-ошибка видна, а Retry на том же экземпляре загружает порт после восстановления.</summary>
    [Fact]
    public async Task InitialSqlError_ShowsRetryAndLoadsAfterRecovery()
    {
        using var fixture = new NetFixture(loadException: SqlExceptionFactory.Create(53, "connection error"));
        var page = fixture.Context.Render<NetPage>();
        page.WaitForAssertion(() => Assert.Contains("Не удалось загрузить порт локальной сети", page.Markup));
        Assert.Empty(page.FindAll("[data-testid='net-port-caption']"));
        Assert.Empty(page.FindAll(".clay-changed-user"));

        fixture.AddLoad();
        await Invoke(page, "RetryLoadAsync");

        WaitForLoad(page);
        Assert.Equal(10, Model(page.Instance).Id);
    }

    /// <summary>При ошибке B после успешного A старая запись не отображается под новым ID.</summary>
    [Fact]
    public async Task LoadErrorAfterIdChange_DoesNotShowPreviousModel()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        fixture.Enqueue(Script.Error(SqlExceptionFactory.Create(10054, "connection lost")));
        fixture.Enqueue(Script.Error(SqlExceptionFactory.Create(10054, "connection lost")));

        ChangeId(page.Instance, 11);
        await Invoke(page, "LoadCurrentAsync", 11);

        Assert.Empty(page.FindAll("[data-testid='net-port-caption']"));
        Assert.Empty(page.FindAll(".clay-changed-user"));
        Assert.Contains("Не удалось загрузить порт локальной сети", page.Markup);
        Assert.Contains("Повторить", page.Markup);
    }

    /// <summary>No-row при смене ID остаётся отдельным состоянием «не найден».</summary>
    [Fact]
    public async Task MissingAfterIdChange_ShowsNotFoundWithoutPreviousModel()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.Enqueue(Script.Rows());

        ChangeId(page.Instance, 11);
        await Invoke(page, "LoadCurrentAsync", 11);

        Assert.Contains("не найден", page.Markup);
        Assert.Empty(page.FindAll("[data-testid='net-port-caption']"));
    }

    /// <summary>Подтверждение PoE для A после перехода на B не вызывает процедуру ни для одного порта.</summary>
    [Fact]
    public async Task PoeConfirmationAfterIdChange_DoesNotResetEitherPort()
    {
        using var fixture = new NetFixture(poe: 1);
        fixture.AddLoad(poe: 1, id: 11, port: 12);
        var provider = fixture.Context.Render<MudDialogProvider>();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "RestartPoeAsync");
        provider.WaitForAssertion(() => Assert.Contains("Перезапустить PoE", provider.Markup));

        ChangeId(page.Instance, 11);
        await Invoke(page, "LoadCurrentAsync", 11);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.Enqueue(Script.NonQuery());
        await page.InvokeAsync(() => provider.FindAll("button").Single(button => button.TextContent.Trim() == "Да").Click());
        await pending;

        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("sp_ResetPoEport"));
        Assert.Empty(fixture.Connection.TimeoutLog);
    }

    /// <summary>Поздний ручной выбор заказчика имеет приоритет над старым ответом организации сотрудника.</summary>
    [Fact]
    public async Task LateEmployeeOrganization_DoesNotOverwriteManualCustomer()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new NetFixture();
        fixture.Enqueue(new ScriptEntry
        {
            Started = started,
            Release = release,
            Rows = [Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
                ("Employee", "Test"), ("КодЛицаЗаказчика", 1), ("Организация", "Организация 1"))],
        });
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "OnEmployeeChangedAsync", 4);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Invoke(page, "OnCustomerChanged", 2);
        release.SetResult();
        await pending;

        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
    }

    /// <summary>Ответ сотрудника, выбранного на A, не меняет заказчика после перехода на B.</summary>
    [Fact]
    public async Task EmployeeResponseAfterIdChange_DoesNotChangeNewModel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new NetFixture();
        fixture.Enqueue(new ScriptEntry
        {
            Started = started,
            Release = release,
            Rows = [Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
                ("Employee", "Test"), ("КодЛицаЗаказчика", 1))],
        });
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "OnEmployeeChangedAsync", 4);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.AddLoad(id: 11, port: 12, customerId: 2);

        ChangeId(page.Instance, 11);
        var loading = Invoke(page, "LoadCurrentAsync", 11);
        release.SetResult();
        await pending;
        await loading;

        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
        Assert.Null(Model(page.Instance).EmployeeId);
    }

    /// <summary>Примечание розетки A не появляется после перехода на B.</summary>
    [Fact]
    public async Task SocketResponseAfterIdChange_DoesNotChangeNewModel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new NetFixture();
        fixture.Enqueue(new ScriptEntry
        {
            Started = started,
            Release = release,
            Rows = [Script.Row(("КодРозетки", 5), ("Розетка", "A-1"),
                ("Примечание", "old socket"), ("КодРасположения", 55), ("Работает", true))],
        });
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "OnSocketChangedAsync", 5);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.AddLoad(id: 11, port: 12);

        ChangeId(page.Instance, 11);
        var loading = Invoke(page, "LoadCurrentAsync", 11);
        release.SetResult();
        await pending;
        await loading;

        Assert.Null(GetPrivate<string>(page.Instance, "_socketNotes"));
    }

    /// <summary>Ошибка JS refresh после SQL не оставляет dirty-state и не вызывает повторный UPDATE.</summary>
    [Fact]
    public async Task RefreshDisconnectedAfterSuccessfulSave_CompletesSavedState()
    {
        using var fixture = new NetFixture();
        fixture.Context.JSInterop.SetupVoid("clayModelEquipmentForm.refreshOpener")
            .SetException(new Microsoft.JSInterop.JSDisconnectedException("disconnected"));
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.AddSaveDbCommand(Script.Scalar(0));
        fixture.AddLoad(connected: "srv-new");
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Connected = "srv-new";

        await Invoke(page, "SaveAsync", false);

        Assert.Single(fixture.Connection.CommandLog.Where(sql => sql.Contains("UPDATE vwЛокальнаяСеть")));
        Assert.False(IsModified(page.Instance));
        Assert.Contains("Изменения сохранены", page.Markup);
    }

    /// <summary>Mismatch показывает название организации, а OrganizationId=0 не скрывает предупреждение.</summary>
    [Theory]
    [InlineData(1, "Организация 1")]
    [InlineData(0, "Организация 0")]
    public void CompanyMismatch_UsesOrganizationNameIncludingZero(int organizationId, string expected)
    {
        using var fixture = new NetFixture(employeeId: 4, customerId: 2, organizationId: organizationId);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        Assert.Contains("Компания сотрудника", page.Markup);
        Assert.Contains(expected, page.Markup);
    }

    /// <summary>Поздний текст остаётся изменённым после отправки и не закрывает форму по ОК.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Save_DoesNotAcceptTextEnteredAfterDispatch(bool closeAfterSave, bool concurrentChange)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.AddSaveDbCommand(new ScriptEntry { Scalar = 0, Started = started, Release = release });
        var freshChangedAt = new DateTime(2026, 11, 21, 11, 0, 0);
        fixture.AddLoad(description: concurrentChange ? "чужое изменение" : "отправлено",
            customerId: 2, changedAt: freshChangedAt, includeCustomerLookup: !concurrentChange);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var originalChangedAt = Model(page.Instance).ChangedAtUtc;
        await page.FindAll("textarea")[1].InputAsync(new ChangeEventArgs { Value = "отправлено" });

        var save = Invoke(page, "SaveAsync", closeAfterSave);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await page.FindAll("textarea")[1].InputAsync(new ChangeEventArgs { Value = "после отправки" });
        }
        finally
        {
            release.TrySetResult();
        }
        await save;
        page.WaitForAssertion(() =>
        {
            var description = page.FindAll("textarea")[1];
            Assert.True(description.Closest(".clay-field-change")?.HasAttribute("data-clay-field-changed"));
        });

        var sent = Assert.Single(fixture.Connection.ParameterLog, args => args.ContainsKey("ExpectedChangedAt"));
        Assert.Equal("отправлено", sent["Description"]);
        Assert.Equal("после отправки", Model(page.Instance).Description);
        Assert.Equal(concurrentChange ? 1 : 2, Model(page.Instance).PersonCustomerId);
        Assert.Equal(concurrentChange ? originalChangedAt : freshChangedAt, Model(page.Instance).ChangedAtUtc);
        if (concurrentChange)
            Assert.Contains("Сервер также изменил", page.Markup);
        Assert.True(IsModified(page.Instance));
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    [Fact]
    public async Task NetDescriptionChangeUsesAcceptedTextAndClearsOnRevert()
    {
        using var fixture = new NetFixture(description: "Исходный текст");
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        const string marker = "data-clay-field-changed";
        Assert.Empty(page.FindAll($"[{marker}]"));

        await page.InvokeAsync(() => page.FindAll("textarea")[1].InputAsync("Изменено"));
        page.WaitForAssertion(() => Assert.True(page.FindAll("textarea")[1]
            .Closest(".clay-field-change")?.HasAttribute(marker)));

        await page.InvokeAsync(() => page.FindAll("textarea")[1].InputAsync("Исходный текст"));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll($"[{marker}]")));
    }

    /// <summary>Поздний ответ начальной загрузки A не публикует её организацию для B.</summary>
    [Fact]
    public async Task InitialEmployeeResponseAfterRouteChange_DoesNotPublishOldOrganization()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        var employeeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEmployee = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AddLoad(employeeId: 5, initialEmployeeLookup: new ScriptEntry
        {
            Started = employeeStarted,
            Release = releaseEmployee,
            Rows = [Script.Row(("КодСотрудника", 5), ("КодЛицаЗаказчика", 9), ("Организация", "Старая"))],
        }, includeCustomerLookup: false);
        var pendingA = Invoke(page, "LoadCurrentAsync", 10);
        await employeeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var secondLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authentication = (FakeAuthenticationStateProvider)fixture.Context.Services.GetRequiredService<AuthenticationStateProvider>();
        authentication.BeforeRead = async () =>
        {
            secondLoadStarted.TrySetResult();
            await releaseSecondLoad.Task;
        };
        fixture.AddLoad(id: 11, port: 12, customerId: 3, employeeId: 6, organizationId: 2);
        ChangeId(page.Instance, 11);
        var pendingB = Invoke(page, "LoadCurrentAsync", 11);
        try
        {
            await secondLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            releaseEmployee.TrySetResult();
            await pendingA;
            Assert.Null(GetPrivate<int?>(page.Instance, "_employeeOrganizationId"));
            Assert.Null(GetPrivate<string>(page.Instance, "_employeeOrganizationName"));
        }
        finally
        {
            releaseEmployee.TrySetResult();
            releaseSecondLoad.TrySetResult();
            await pendingB;
        }

        Assert.Equal(11, Model(page.Instance).Id);
        Assert.Equal(2, GetPrivate<int?>(page.Instance, "_employeeOrganizationId"));
        Assert.Equal("Организация 2", GetPrivate<string>(page.Instance, "_employeeOrganizationName"));
    }

    /// <summary>Выбор по отдельным полям разрешает только той версии сверки, которую видел пользователь.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfirmedSave_ReconcilesFieldsWithoutReplayingWrite(bool serverChanged, bool localChanged)
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
        fixture.AddSaveDbCommand(new ScriptEntry { Scalar = 0, Started = writeStarted, Release = releaseWrite });
        var reviewedVersion = new DateTime(2026, 11, 21, 11, 0, 0);
        fixture.AddLoad(connected: "общая запись", description: "запись из БД",
            changedAt: reviewedVersion, includeCustomerLookup: false);

        await TypeText(page, 0, "подключено на момент отправки");
        await TypeText(page, 1, "отправленное примечание");
        var save = Invoke(page, "SaveAsync", false);
        try
        {
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await TypeText(page, 0, "поздняя связь");
            await TypeText(page, 1, "позднее примечание");
        }
        finally
        {
            releaseWrite.TrySetResult();
        }
        await save;

        var review = page.Find("[data-testid='net-reconciliation']");
        Assert.Contains("отправленное примечание", review.TextContent);
        Assert.Contains("позднее примечание", review.TextContent);
        Assert.Contains("запись из БД", review.TextContent);
        const string acceptText = "Принять результаты сверки";
        Assert.True(ReconciliationButton(page, acceptText).HasAttribute("disabled"));
        var choices = page.FindComponents<ClayRadioGroup<bool?>>();
        Assert.Equal(2, choices.Count);
        await page.InvokeAsync(() => choices[0].Instance.ValueChanged.InvokeAsync(true));
        await page.InvokeAsync(() => choices[1].Instance.ValueChanged.InvokeAsync(false));
        Assert.False(ReconciliationButton(page, acceptText).HasAttribute("disabled"));

        fixture.AddLoad(connected: "общая запись", description: serverChanged ? "новый ответ БД" : "запись из БД",
            changedAt: serverChanged ? reviewedVersion.AddMinutes(1) : reviewedVersion, includeCustomerLookup: false);
        if (localChanged)
        {
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var authentication = (FakeAuthenticationStateProvider)fixture.Context.Services.GetRequiredService<AuthenticationStateProvider>();
            authentication.BeforeRead = async () =>
            {
                readStarted.TrySetResult();
                await releaseRead.Task;
            };
            var confirm = ReconciliationButton(page, acceptText).ClickAsync();
            try
            {
                await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await TypeText(page, 1, "ещё более поздний ввод");
            }
            finally
            {
                releaseRead.TrySetResult();
            }
            await confirm;
        }
        else
        {
            await ReconciliationButton(page, acceptText).ClickAsync();
        }

        var model = Model(page.Instance);
        Assert.Equal("поздняя связь", model.Connected);
        if (serverChanged || localChanged)
        {
            Assert.Equal(localChanged ? "ещё более поздний ввод" : "позднее примечание", model.Description);
            Assert.NotEqual(reviewedVersion, model.ChangedAtUtc);
            Assert.True(ReconciliationButton(page, acceptText).HasAttribute("disabled"));
            Assert.Contains(serverChanged ? "новый ответ БД" : "ещё более поздний ввод",
                page.Find("[data-testid='net-reconciliation']").TextContent);
        }
        else
        {
            Assert.Equal("запись из БД", model.Description);
            Assert.Equal(reviewedVersion, model.ChangedAtUtc);
            Assert.Empty(page.FindAll("[data-testid='net-reconciliation']"));
            Assert.True(IsModified(page.Instance));
        }
        Assert.Single(fixture.Connection.ParameterLog, args => args.ContainsKey("ExpectedChangedAt"));
    }

    /// <summary>Повтор чтения после сбоя не повторяет подтверждённую запись и оставляет поздний текст.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedSave_RetriesReadWithoutReplayingWrite(bool confirmedEmployee)
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
        fixture.AddSaveDbCommand(new ScriptEntry { Scalar = 0, Started = writeStarted, Release = releaseWrite });
        fixture.Enqueue(new ScriptEntry { Exception = SqlExceptionFactory.Create(208, "fixture read failure") });

        await TypeText(page, 1, "отправленное примечание");
        var save = Invoke(page, "SaveAsync", false);
        try
        {
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await TypeText(page, 1, "позднее примечание");
        }
        finally
        {
            releaseWrite.TrySetResult();
        }
        await save;
        Assert.Contains("перечитать порт не удалось", page.Markup);
        var freshVersion = new DateTime(2026, 11, 21, 11, 0, 0);
        var originalVersion = Model(page.Instance).ChangedAtUtc;
        var previousReads = fixture.Connection.CommandLog.Count;
        if (confirmedEmployee)
            fixture.AddLoad(description: "отправленное примечание", changedAt: freshVersion, includeCustomerLookup: false);
        else
            fixture.Enqueue(Script.Rows());
        await ReconciliationButton(page, "Сверить с БД").ClickAsync();

        Assert.Equal(confirmedEmployee ? freshVersion : originalVersion, Model(page.Instance).ChangedAtUtc);
        if (!confirmedEmployee)
        {
            Assert.Equal(previousReads + 1, fixture.Connection.CommandLog.Count);
            Assert.Contains("текущий пользователь не подтверждён", page.Markup);
        }
        Assert.Equal("позднее примечание", Model(page.Instance).Description);
        Assert.True(IsModified(page.Instance));
        Assert.Equal(confirmedEmployee ? 0 : 1, page.FindAll("[data-testid='net-reconciliation']").Count);
        Assert.Single(fixture.Connection.ParameterLog, args => args.ContainsKey("ExpectedChangedAt"));
    }

    private static Task TypeText(IRenderedComponent<NetPage> page, int index, string value) =>
        page.FindAll("textarea")[index].InputAsync(new ChangeEventArgs { Value = value });

    private static AngleSharp.Dom.IElement ReconciliationButton(IRenderedComponent<NetPage> page, string text) =>
        page.FindAll("[data-testid='net-reconciliation'] button").Single(button => button.TextContent.Trim() == text);

    /// <summary>Открытие карточки с сохранённой попыткой блокирует обе команды независимо от статуса отправки.</summary>
    [Theory]
    [InlineData("running")]
    [InlineData("unknown")]
    public async Task RestoredOperation_BlocksSaveAndPoe(string status)
    {
        using var fixture = new NetFixture(poe: 1, restoredOperation: OperationJson("net.poe", status));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        await TypeText(page, 1, "локальный ввод");
        Assert.Contains("Результат команды «Перезапустить PoE» неизвестен", page.Markup);
        var count = fixture.Connection.CommandLog.Count;

        await Invoke(page, "SaveAsync", false);
        await Invoke(page, "RestartPoeAsync");

        Assert.Equal(count, fixture.Connection.CommandLog.Count);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Перед RAM-регистрацией старое свидетельство становится серверной блокировкой без восстановления ввода.</summary>
    [Fact]
    public async Task LegacyEvidenceBlocksNewRamEditorWithoutRestoringDraft()
    {
        var id = Guid.NewGuid();
        using var fixture = new NetFixture(poe: 1, legacyEvidence:
            [new(id.ToString("N"), "net.poe", "10")], includeCustomerLookup: false);
        fixture.AddPoeRead();
        fixture.Enqueue(Script.Rows(Script.Row(("КодЛица", 1), ("Кличка", "Тестовая организация"))));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Assert.DoesNotContain("Открыть сохранённую попытку", page.Markup);
        var count = fixture.Connection.CommandLog.Count;

        await TypeText(page, 1, "черновик");
        await Invoke(page, "SaveAsync", false);
        await Invoke(page, "RestartPoeAsync");

        Assert.Contains("В серверном журнале есть незавершённая операция с портом", page.Markup);
        Assert.Equal(count, fixture.Connection.CommandLog.Count);
        Assert.True(fixture.ServerJournal.HasLegacyEvidence(id));
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.recoverListedDraft"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Непроверенное свидетельство блокирует запись до регистрации RAM.</summary>
    [Fact]
    public async Task WrongLegacyEvidenceCannotOpenRamEditor()
    {
        using var fixture = new NetFixture(poe: 1, legacyEvidence:
            [new(Guid.NewGuid().ToString("N"), "net.poe", "99")]);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Assert.DoesNotContain("Открыть сохранённую попытку", page.Markup);
        Assert.Contains("Журнал операций недоступен", page.Markup);

        await Invoke(page, "SaveAsync", false);
        await Invoke(page, "RestartPoeAsync");

        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.recoverListedDraft"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Без подтверждённого Begin SQL не отправляется.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Save_MissingJournalAcknowledgementDoesNotDispatch(bool failBegin, bool mismatchedBegin)
    {
        using var fixture = new NetFixture(failBegin: failBegin, mismatchedBegin: mismatchedBegin);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        await TypeText(page, 1, "фикстура не должна попасть в журнал открытым текстом");

        await Invoke(page, "SaveAsync", false);

        Assert.Contains("Команда не отправлена", page.Markup);
        var intent = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.DoesNotContain("фикстура", (string)intent.Arguments[1]!);
        Assert.Contains("Fingerprint", (string)intent.Arguments[1]!);
        AssertNetBeginBlocksOtherDrafts(intent);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть"));
    }

    /// <summary>Браузерная отметка завершает журнал только с подтверждением точной серверной попытки.</summary>
    [Theory]
    [InlineData(InventoryOperationState.Confirmed, 10, true)]
    [InlineData(InventoryOperationState.Confirmed, 99, false)]
    [InlineData(InventoryOperationState.Dispatching, null, false)]
    [InlineData(null, null, false)]
    public async Task RestoredConfirmedMarkerRequiresExactServerReceipt(
        InventoryOperationState? state, int? resultId, bool confirmed)
    {
        var attemptId = Guid.NewGuid();
        using var fixture = new NetFixture(poe: 1, restoredOperation: OperationJson("net.poe", "confirmed", attemptId.ToString("N")));
        if (state is { } journalState)
            fixture.ServerJournal.SeedPoe(attemptId, 10, journalState, resultId);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Assert.Contains("Требуется сверить сохранённое подтверждение", page.Markup);
        fixture.AddPoeRead();
        if (!confirmed)
            fixture.AddPoeRead();

        await page.InvokeAsync(() => OperationJournalButton(page, "Проверить журнал операций").ClickAsync());

        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        var commits = fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        if (confirmed)
        {
            Assert.Equal(9L, Assert.Single(commits).Arguments[1]);
            Assert.Contains("Сервер подтвердил успешный ответ на команду PoE", page.Markup);
        }
        else
        {
            Assert.Empty(commits);
            Assert.True(NetReadinessButton(page, "Перезапустить PoE").HasAttribute("disabled"));
            Assert.Contains("Повтор заблокирован", page.Markup);
        }
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("sp_ResetPoEport"));
    }

    private static void AssertNetBeginBlocksOtherDrafts(JSRuntimeInvocation begin)
    {
        using var payload = System.Text.Json.JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.True(payload.RootElement.GetProperty("blockOtherDraftOperations").GetBoolean());
    }

    private static AngleSharp.Dom.IElement OperationJournalButton(IRenderedComponent<NetPage> page, string text) =>
        page.FindAll("[data-testid='net-operation-journal'] button").Single(button => button.TextContent.Trim() == text);

    private static void Render(IRenderedComponent<NetPage> page) => page.InvokeAsync(() =>
        typeof(NetPage).GetMethod("StateHasChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(page.Instance, null));

    private static void WaitForLoad(IRenderedComponent<NetPage> page) =>
        page.WaitForAssertion(() => Assert.Equal("clay-test-net_2",
            page.Find("[data-testid='net-port-caption']").TextContent), TimeSpan.FromSeconds(3));

    private static LocalNetworkPort Model(NetPage page) => GetPrivate<LocalNetworkPort>(page, "_model")!;

    private static bool IsModified(NetPage page) => (bool)typeof(NetPage)
        .GetProperty("IsModified", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page)!;

    private static void ChangeId(NetPage page, int id)
    {
        page.Id = id;
        typeof(NetPage).GetField("_loadedId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(page, id);
    }

    private static T? GetPrivate<T>(NetPage page, string name) => (T?)typeof(NetPage)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page);

    private static Task Invoke(IRenderedComponent<NetPage> page, string name, params object?[] args) =>
        page.InvokeAsync(async () =>
        {
            var method = typeof(NetPage).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, args) is Task task)
                await task;
        });

    private static string OperationJson(string kind, string status, string attemptId = "net-fixture-attempt") =>
        $$"""{"version":1,"attemptId":"{{attemptId}}","kind":"{{kind}}","status":"{{status}}","revision":9,"data":"{{(kind == "net.poe" ? "10" : "fixture")}}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";

    private sealed class AcknowledgingJournal(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var result = await inner.InvokeAsync<T>(identifier, cancellationToken, args);
            if (identifier != "clayDraftStore.beginOperationIntent" || result is not string operationJson)
                return result;
            using var intent = System.Text.Json.JsonDocument.Parse((string)args![1]!);
            var acknowledgement = System.Text.Json.Nodes.JsonNode.Parse(operationJson)!;
            acknowledgement["attemptId"] = intent.RootElement.GetProperty("attemptId").GetString();
            acknowledgement["data"] = intent.RootElement.GetProperty("data").GetString();
            if (intent.RootElement.GetProperty("acceptedScopes").GetArrayLength() > 0)
                acknowledgement["scopes"] = System.Text.Json.Nodes.JsonNode.Parse(
                    """[{"name":"net-input","typedRevision":3,"rawRevisions":{}}]""");
            return (T)(object)acknowledgement.ToJsonString();
        }
    }

    private sealed class NetFixture : IDisposable
    {
        private readonly Queue<ScriptEntry> _entries;

        public NetFixture(
            int id = 10,
            string connected = "",
            string description = "",
            byte poe = 0,
            bool currentUser = true,
            ScriptEntry? linkEntry = null,
            int customerId = 1,
            int? employeeId = null,
            int organizationId = 1,
            Exception? loadException = null,
            string? restoredOperation = null,
            ClayLegacyDraftOperationEvidence[]? legacyEvidence = null,
            bool failBegin = false,
            bool mismatchedBegin = false,
            string? restoredInput = null,
            bool restoredRaw = false,
            bool rawPending = false,
            Func<bool>? hasLateRaw = null,
            bool missingPort = false,
            Exception? portReadError = null,
            bool deferRaw = false,
            bool includeCustomerLookup = true)
        {
            DapperColumnMapper.Initialize();
            _entries = new Queue<ScriptEntry>();
            if (loadException is not null)
                _entries.Enqueue(new ScriptEntry { Exception = loadException });
            else if (missingPort || portReadError is not null)
            {
                _entries.Enqueue(currentUser ? Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."))) : Script.Rows());
                _entries.Enqueue(portReadError is null ? Script.Rows() : Script.Error(portReadError));
            }
            else
                AddLoad(connected, description, poe, currentUser, linkEntry,
                    id: id, customerId: customerId, employeeId: employeeId, organizationId: organizationId,
                    includeCustomerLookup: includeCustomerLookup);
            Connection = new ScriptedConnection(_entries, emulateEquipmentIdentity: false);
            ServerJournal = new NetJournalConnection(Connection);
            Context = new BunitContext();
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            Context.Services.AddScoped(_ => new ClayDraftService(mismatchedBegin
                ? Context.JSInterop.JSRuntime : new AcknowledgingJournal(Context.JSInterop.JSRuntime)));
            Context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
                .SetResult(legacyEvidence ?? []);
            Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
                .SetResult(new ClayDraftRestore("net-fixture", restoredOperation is not null || restoredInput is not null || restoredRaw,
                    0, null, restoredOperation, restoredInput is null ? null : new Dictionary<string, string> { ["net-input"] = restoredInput }));
            Context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(3);
            Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => hasLateRaw?.Invoke() == true)
                .SetResult(new ClayDraftEditorScopeState(4, true, restoredOperation is not null));
            Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => hasLateRaw?.Invoke() != true)
                .SetResult(new ClayDraftEditorScopeState(3, restoredRaw, restoredOperation is not null));
            if (!deferRaw)
                Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                    .SetResult(new ClayDraftRawApplyResult([], rawPending ? ["data:net.return-date:0"] : []));
            Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], rawPending ? ["data:net.return-date:0"] : []));
            Context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(true);
            Context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
                .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
            Context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
                .SetResult(new ClayDraftRestoreReadiness(!rawPending, rawPending ? ["net-input"] : [], []));
            foreach (var kind in new[] { "net.save", "net.poe" })
            {
                var begin = Context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent",
                        args => ((string)args.Arguments[1]!).Contains($"\"kind\":\"{kind}\"", StringComparison.Ordinal));
                if (failBegin)
                    begin.SetException(new Microsoft.JSInterop.JSException("fixture lost begin"));
                else
                    begin.SetResult(OperationJson(kind, "running"));
            }
            Context.Services.AddMudServices();
            Context.Services.AddMudExtensions();
            var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => ServerJournal);
            Context.Services.AddScoped(_ => db);
            Context.Services.AddSingleton(new NetSocketSelectDataSource(db));
            Context.Services.AddSingleton(new PhoneEmployeeSelectDataSource(db));
            Context.Services.AddSingleton(new PhoneCustomerSelectDataSource(db));
            Context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["URI_equipment_form"] = "http://localhost/equipment",
                    ["URI_user_form"] = "http://localhost/user",
                }).Build());
            Context.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthenticationStateProvider());
            Context.Services.AddSingleton(new ClayFormWindowService(Context.JSInterop.JSRuntime));
            Navigation = new FakeNavigationManager();
            Navigation.NavigateTo($"http://localhost/Net/Net?id={id}");
            Context.Services.AddSingleton<NavigationManager>(Navigation);
        }

        public BunitContext Context { get; }
        public ScriptedConnection Connection { get; }
        public NetJournalConnection ServerJournal { get; }
        public FakeNavigationManager Navigation { get; }

        public void AddLoad(
            string connected = "",
            string description = "",
            byte poe = 0,
            bool currentUser = true,
            ScriptEntry? linkEntry = null,
            int id = 10,
            byte port = 2,
            int customerId = 1,
            int? employeeId = null,
            int organizationId = 1,
            ScriptEntry? initialEmployeeLookup = null,
            bool includeCustomerLookup = true,
            DateTime? changedAt = null,
            int actorId = 4, int equipmentId = 8)
        {
            _entries.Enqueue(currentUser
                ? Script.Rows(Script.Row(("КодСотрудника", actorId), ("Сотрудник", "Тестов А."),
                    ("Employee", "Test"), ("КодЛицаЗаказчика", 1)))
                : Script.Rows());
            _entries.Enqueue(Script.Rows(Script.Row(
                ("КодЛокальнойСети", id), ("КодОборудования", equipmentId), ("Порт", port), ("PoE", poe),
                ("КодРозетки", null), ("КодЛицаЗаказчика", customerId), ("КодСотрудника", employeeId),
                ("Подключено", connected), ("Примечания", description), ("СрокВозврата", null),
                ("Изменил", 4), ("Сотрудник", "Тестов А."), ("Изменено", changedAt ?? new DateTime(2026, 11, 21, 10, 0, 0)))));
            _entries.Enqueue(Script.Rows(Script.Row(("СетевоеИмя", "clay-test-net"))));
            _entries.Enqueue(Script.Rows(Script.Row(("КодРасположения", 50))));
            _entries.Enqueue(Script.Rows(
                Script.Row(("КодЛокальнойСети", 11), ("Строка", "sw-a, порт:1"))));
            _entries.Enqueue(Script.Rows(
                Script.Row(("КодЛокальнойСети", 11), ("Строка", "sw-a, порт:1")),
                Script.Row(("КодЛокальнойСети", 12), ("Строка", "sw-a, порт:2"))));
            if (employeeId is > 0)
                _entries.Enqueue(initialEmployeeLookup ?? Script.Rows(Script.Row(("КодСотрудника", employeeId.Value),
                    ("Сотрудник", "Тестов А."), ("Employee", "Test"),
                    ("КодЛицаЗаказчика", organizationId), ("Организация", $"Организация {organizationId}"))));
            if (NetEquipmentLinkExtractor.Extract(connected).Count > 0 || NetEquipmentLinkExtractor.Extract(description).Count > 0)
                _entries.Enqueue(linkEntry ?? Script.Rows());
            if (includeCustomerLookup)
                _entries.Enqueue(Script.Rows(Script.Row(("КодЛица", customerId), ("Кличка", "Тестовая организация"))));
        }

        public void Enqueue(ScriptEntry entry) => _entries.Enqueue(entry);

        public void AddPoeRestart(ScriptEntry command)
        {
            Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
            AddPoeRead();
            AddPoeRead();
            Enqueue(command);
        }

        public void AddPoeRead()
        {
            Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
            Enqueue(Script.Rows(Script.Row(("КодЛокальнойСети", 10), ("PoE", (byte)1))));
        }

        public void AddSaveDbCommand(ScriptEntry command)
        {
            Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
            Enqueue(Script.Rows(Script.Row(("КодЛокальнойСети", 10), ("Изменено", new DateTime(2026, 11, 21, 10, 0, 0)))));
            Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));
            Enqueue(command);
        }

        public void Dispose() => Context.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    internal sealed class NetJournalConnection(ScriptedConnection inner) : DbConnection
    {
        private readonly Dictionary<Guid, JournalRow> _rows = [];
        private readonly Dictionary<Guid, JournalRow> _legacyRows = [];
        private readonly Dictionary<Guid, HashSet<string>> _scopes = [];
        public bool LoseNextConfirmedCommitReply { get; set; }
        public bool HasLegacyEvidence(Guid attemptId) => _legacyRows.ContainsKey(attemptId);
        public InventoryOperationState? PoeState => _rows.Values.LastOrDefault(row => row.CommandKind == "net.poe")?.State;
        public InventoryOperationState? StateFor(Guid attemptId) => _rows.TryGetValue(attemptId, out var row) ? row.State : null;
        public IReadOnlyCollection<string> ScopesFor(Guid attemptId) =>
            _scopes.TryGetValue(attemptId, out var scopes) ? scopes : Array.Empty<string>();

        public void SeedUnresolvedPoe(int portId) =>
            SeedPoe(Guid.NewGuid(), portId, InventoryOperationState.Dispatching, null);

        public void SeedPoe(Guid attemptId, int portId, InventoryOperationState state, int? resultId) =>
            _rows[attemptId] = new(attemptId, 4, $"net/{portId}", "net.poe", state, resultId);

        public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void Open() => inner.Open();
        public override void Close() => inner.Close();
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        protected override DbCommand CreateDbCommand() => new JournalCommand(this, inner.CreateCommand());

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            Assert.Equal(IsolationLevel.Serializable, isolationLevel);
            return new JournalTransaction(this);
        }

        private sealed record JournalRow(Guid AttemptId, int EmployeeId, string ConflictKey, string CommandKind,
            InventoryOperationState State, int? ResultEntityId)
        {
            public bool Pending => State is not (InventoryOperationState.Confirmed or InventoryOperationState.Rejected);
        }

        private sealed class JournalTransaction(NetJournalConnection owner) : DbTransaction
        {
            public JournalRow? Reservation { get; set; }
            public JournalRow? LegacyEvidence { get; set; }
            public List<(Guid AttemptId, string Scope)> ScopeAdditions { get; } = [];
            public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
            protected override DbConnection DbConnection => owner;
            public override void Commit()
            {
                if (Reservation is { } row) owner._rows[row.AttemptId] = row;
                if (LegacyEvidence is { } evidence) owner._legacyRows[evidence.AttemptId] = evidence;
                foreach (var (id, scope) in ScopeAdditions)
                {
                    if (!owner._scopes.TryGetValue(id, out var scopes))
                        owner._scopes[id] = scopes = [];
                    scopes.Add(scope);
                }
                if (owner.LoseNextConfirmedCommitReply && Reservation is { State: InventoryOperationState.Confirmed })
                {
                    owner.LoseNextConfirmedCommitReply = false;
                    throw new TimeoutException("Ответ COMMIT потерян после фиксации результата.");
                }
            }
            public override void Rollback() => Reservation = null;
        }

        private sealed class JournalCommand(NetJournalConnection owner, DbCommand command) : DbCommand
        {
            private bool JournalCommandText => CommandText.Contains("dbo.ClayzorOperationJournal", StringComparison.Ordinal) ||
                CommandText.Contains("dbo.ClayzorOperationScope", StringComparison.Ordinal) ||
                CommandText.Contains("dbo.ClayzorLegacyOperationEvidence", StringComparison.Ordinal) ||
                CommandText.Contains("dbo.ClayzorLegacyOperationScope", StringComparison.Ordinal);
            public override string CommandText { get => command.CommandText; set => command.CommandText = value; }
            public override int CommandTimeout { get => command.CommandTimeout; set => command.CommandTimeout = value; }
            public override CommandType CommandType { get => command.CommandType; set => command.CommandType = value; }
            public override bool DesignTimeVisible { get => command.DesignTimeVisible; set => command.DesignTimeVisible = value; }
            public override UpdateRowSource UpdatedRowSource { get => command.UpdatedRowSource; set => command.UpdatedRowSource = value; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection => command.Parameters;
            protected override DbParameter CreateDbParameter() => command.CreateParameter();
            public override void Prepare() => command.Prepare();
            public override void Cancel() => command.Cancel();

            public override int ExecuteNonQuery()
            {
                if (!JournalCommandText) return command.ExecuteNonQuery();
                Assert.Contains("INSERT INTO dbo.ClayzorOperationJournal", CommandText);
                var tx = Assert.IsType<JournalTransaction>(DbTransaction);
                tx.Reservation = new(Parameter<Guid>("AttemptId"), Parameter<int>("EmployeeId"),
                    Parameter<string>("ConflictKey"), Parameter<string>("CommandKind"), InventoryOperationState.Reserved, null);
                return 1;
            }

            public override object? ExecuteScalar()
            {
                if (!JournalCommandText) return command.ExecuteScalar();
                if (CommandText.Contains("INSERT INTO dbo.ClayzorOperationScope", StringComparison.Ordinal) ||
                    CommandText.Contains("INSERT INTO dbo.ClayzorLegacyOperationScope", StringComparison.Ordinal))
                {
                    Assert.IsType<JournalTransaction>(DbTransaction).ScopeAdditions.Add(
                        (Parameter<Guid>("AttemptId"), Parameter<string>("ConflictKey")));
                    return 1;
                }
                if (CommandText.Contains("UPDATE dbo.ClayzorOperationScope", StringComparison.Ordinal))
                    return owner.ScopesFor(Parameter<Guid>("AttemptId")).Count;
                Assert.Contains("UPDATE dbo.ClayzorOperationJournal", CommandText);
                if (!owner._rows.TryGetValue(Parameter<Guid>("AttemptId"), out var row) ||
                    row.ConflictKey != Parameter<string>("ConflictKey") || row.EmployeeId != Parameter<int>("EmployeeId") ||
                    row.CommandKind != Parameter<string>("CommandKind") || (byte)row.State != Parameter<byte>("ExpectedState"))
                    return 0;
                var next = row with
                {
                    State = (InventoryOperationState)Parameter<byte>("NextState"),
                    ResultEntityId = Parameters["ResultEntityId"].Value as int?,
                };
                if (DbTransaction is JournalTransaction transaction) transaction.Reservation = next;
                else owner._rows[row.AttemptId] = next;
                return 1;
            }

            public override Task<int> ExecuteNonQueryAsync(CancellationToken ct) => JournalCommandText
                ? Task.FromResult(ExecuteNonQuery()) : command.ExecuteNonQueryAsync(ct);

            public override Task<object?> ExecuteScalarAsync(CancellationToken ct)
            {
                if (JournalCommandText) return Task.FromResult(ExecuteScalar());
                if (CommandText.Contains("UPDATE vwЛокальнаяСеть", StringComparison.Ordinal))
                    Assert.IsType<JournalTransaction>(DbTransaction);
                return command.ExecuteScalarAsync(ct);
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
                JournalCommandText ? ReadJournal() : command.ExecuteReader(behavior);

            protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct) =>
                JournalCommandText ? Task.FromResult(ReadJournal()) : command.ExecuteReaderAsync(behavior, ct);

            private DbDataReader ReadJournal()
            {
                if (CommandText.Contains("INSERT INTO dbo.ClayzorLegacyOperationEvidence", StringComparison.Ordinal))
                {
                    Assert.IsType<JournalTransaction>(DbTransaction).LegacyEvidence = new(Parameter<Guid>("AttemptId"),
                        Parameter<int>("EmployeeId"), Parameter<string>("ConflictKey"), Parameter<string>("CommandKind"),
                        InventoryOperationState.Unknown, null);
                    return new ScriptedDataReader([Script.Row(("Value", true))]);
                }
                if (CommandText.Contains("FROM dbo.ClayzorOperationScope", StringComparison.Ordinal) &&
                    !CommandText.Contains("dbo.ClayzorOperationJournal", StringComparison.Ordinal))
                    return new ScriptedDataReader(owner.ScopesFor(Parameter<Guid>("AttemptId"))
                        .Select(scope => Script.Row(("ConflictKey", scope))).ToList());
                if (CommandText.Contains("WHERE ConflictKey = @ConflictKey AND OpenSlot = @OpenSlot", StringComparison.Ordinal))
                {
                    var scope = Parameter<string>("ConflictKey");
                    return new ScriptedDataReader([Script.Row(("Value", owner._rows.Values.Concat(owner._legacyRows.Values)
                        .Any(row => row.Pending && (row.ConflictKey == scope || owner.ScopesFor(row.AttemptId).Contains(scope)))))]);
                }

                JournalRow? row;
                if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalLatestByKind)
                {
                    Assert.Contains("WHERE EmployeeId = @EmployeeId AND ConflictKey = @ConflictKey AND CommandKind = @CommandKind", CommandText);
                    row = owner._rows.Values.LastOrDefault(item => item.EmployeeId == Parameter<int>("EmployeeId") &&
                        item.ConflictKey == Parameter<string>("ConflictKey") && item.CommandKind == Parameter<string>("CommandKind"));
                }
                else
                {
                    Assert.Contains("WHERE AttemptId = @AttemptId", CommandText);
                    owner._rows.TryGetValue(Parameter<Guid>("AttemptId"), out row);
                }
                var table = new DataTable();
                table.Columns.Add("AttemptId", typeof(Guid));
                table.Columns.Add("EmployeeId", typeof(int));
                table.Columns.Add("ConflictKey", typeof(string));
                table.Columns.Add("CommandKind", typeof(string));
                table.Columns.Add("State", typeof(byte));
                table.Columns.Add("ResultEntityId", typeof(int));
                if (row is not null)
                    table.Rows.Add(row.AttemptId, row.EmployeeId, row.ConflictKey, row.CommandKind, (byte)row.State,
                        (object?)row.ResultEntityId ?? DBNull.Value);
                return table.CreateDataReader();
            }

            private T Parameter<T>(string name) => Assert.IsType<T>(Parameters[name].Value);

            protected override void Dispose(bool disposing)
            {
                if (disposing) command.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    private sealed class FakeAuthenticationStateProvider : AuthenticationStateProvider
    {
        /// <inheritdoc />
        public Func<Task>? BeforeRead { get; set; }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\devadmin")], "Negotiate");
            if (BeforeRead is not null)
                await BeforeRead();
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
    }
}
