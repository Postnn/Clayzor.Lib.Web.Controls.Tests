using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Pages.Maintenance;
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

/// <summary>Проверки отображения и цепочки подтверждения регламентной работы.</summary>
public sealed partial class MaintenanceTests
{
    /// <summary> NULL и обе границы часа используют правила legacy.</summary>
    [Theory]
    [InlineData(null, "Normal")]
    [InlineData(-1, "Error")]
    [InlineData(0, "Warning")]
    [InlineData(3599, "Warning")]
    [InlineData(3600, "Normal")]
    [InlineData(9000, "Normal")]
    public void DeadlineUsesSqlServerClock(int? offset, string expected)
    {
        var now = new DateTime(2026, 11, 25, 15, 0, 0);
        Assert.Equal(expected, MaintenancePresentation.DeadlineSeverity(offset is { } n ? now.AddSeconds(n) : null, now).ToString());
    }

    /// <summary>Отклоняет небезопасную схему, файловые и неполные веб-адреса.</summary>
    [Theory]
    [InlineData("  HTTPS://example.test/instructions  ", true)]
    [InlineData("https:example.test", false)]
    [InlineData("http://user@example.test", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData(@"C:\docs\instructions.html", false)]
    [InlineData(@"\\host\docs\instructions.html", false)]
    public void LinkUsesOnlyWebSchemes(string value, bool expected) =>
        Assert.Equal(expected, MaintenancePresentation.WebInstruction(value) is not null);

    /// <summary>«Нет» и закрытие сохраняют грид и не вызывают INSERT.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDoesNotWrite(bool close)
    {
        await using var context = CreateContext(out var connection);
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        var click = ClickConfirmation(page);
        provider.WaitForAssertion(() => Assert.Contains("Работа <test>", provider.Markup.Replace("&lt;", "<").Replace("&gt;", ">")));
        var button = close ? provider.Find(".mud-button-close") :
            provider.FindAll("button").Single(x => x.TextContent.Trim() == "Нет");
        await button.ClickAsync(new MouseEventArgs());
        await click;
        Assert.Equal(0, connection.InsertCount);
        Assert.Contains("Регламентная работа", page.Markup);
    }

    /// <summary>Дублирующий вызов во время INSERT блокируется. Ошибка чтения не превращает запись в неуспех.</summary>
    [Fact]
    public async Task ConfirmWritesOnceAndReportsFailedReload()
    {
        await using var context = CreateContext(out var connection);
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        var click = ClickConfirmation(page);
        var confirm = WaitForYes(provider);
        connection.InsertGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.FailReadAfterInsert = true;
        var confirmClick = confirm.ClickAsync(new MouseEventArgs());
        try
        {
            await connection.InsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.InvokeAsync(() => Action(page).Instance.OnClick.InvokeAsync());
            Assert.Equal(1, connection.InsertCount);
        }
        finally { connection.InsertGate.TrySetResult(); }
        await Task.WhenAll(click, confirmClick);
        Assert.Equal(1, connection.InsertCount);
        Assert.Equal(17, connection.CompletedId);
        Assert.Contains("Выполнение записано, список не обновлён", page.Markup);
    }

    /// <summary>Отзыв роли и исчезновение работы после открытия вопроса блокируют команду.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task YesRechecksAuthorizationAndAvailability(bool revokeRole)
    {
        await using var context = CreateContext(out var connection);
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        var click = ClickConfirmation(page);
        var confirm = WaitForYes(provider);
        if (revokeRole) connection.HasRole = false;
        else connection.WorkAvailable = false;
        await confirm.ClickAsync(new MouseEventArgs());
        await click;
        Assert.Equal(0, connection.InsertCount);
        Assert.Contains(revokeRole ? "нет права" : "Работа удалена", page.Markup);
    }

    /// <summary>Неизвестный исход сохраняет маркер и блокирует слепой повтор.</summary>
    [Fact]
    public async Task TimeoutPreservesUnknownAndDisablesAnotherInsert()
    {
        await using var context = CreateContext(out var connection);
        connection.FailInsert = true;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        var click = ClickConfirmation(page);
        await WaitForYes(provider).ClickAsync(new MouseEventArgs());
        await click;
        await page.InvokeAsync(() => Action(page).Instance.OnClick.InvokeAsync());
        Assert.Equal(1, connection.InsertCount);
        Assert.True(Action(page).Instance.Disabled);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Contains("Исход выполнения требует проверки", page.Markup);
        Assert.Contains("Работа №17", page.Markup);
        Assert.Contains("maintenance-test", page.Markup);
        await RecoveryButton(page, "Перечитать список").ClickAsync(new MouseEventArgs());
        Assert.True(Action(page).Instance.Disabled);
        Assert.Equal(1, connection.InsertCount);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.retryStorage"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Ошибка после успешного INSERT требует только повторной записи подтверждения в хранилище.</summary>
    [Fact]
    public async Task ConfirmedInsertRetriesOnlyCleanupWithSameRevision()
    {
        await using var context = CreateContext(out var connection);
        var first = true;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!first) return false;
            first = false;
            return true;
        }).SetException(new JSException("Ошибка записи IndexedDB"));
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        var click = ClickConfirmation(page);
        await WaitForYes(provider).ClickAsync(new MouseEventArgs());
        await click;

        Assert.Equal(1, connection.InsertCount);
        Assert.True(Action(page).Instance.Disabled);
        using var intent = JsonDocument.Parse((string)Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]).Arguments[1]!);
        Assert.Equal("confirmation", Assert.Single(intent.RootElement.GetProperty("acceptedScopes").EnumerateArray()).GetString());
        await RecoveryButton(page, "Перечитать список").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, connection.InsertCount);
        Assert.False(Action(page).Instance.Disabled);
        Assert.Equal(2L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]).Arguments[1]);
        var commits = context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        Assert.Equal(2, commits.Count);
        Assert.All(commits, commit => Assert.Equal(2L, commit.Arguments[1]));
        Assert.DoesNotContain("Не удалось завершить", page.Markup);
    }

    /// <summary>Подтверждённый исход из новой регистрации продолжает очистку без повторного INSERT.</summary>
    [Fact]
    public async Task ReopenedConfirmedReceiptOnlyRetriesCleanup()
    {
        const string receipt = """{"version":1,"attemptId":"last-session","kind":"maintenance.complete","status":"confirmed","revision":9,"data":"17","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";
        await using var context = CreateContext(out var connection, restoredOperation: receipt);
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.False(Action(page).Instance.Disabled));
        Assert.DoesNotContain("Завершить обработку черновика", page.Markup);

        Assert.Equal(0, connection.InsertCount);
        Assert.False(Action(page).Instance.Disabled);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(9L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
    }

    /// <summary>Receipt восстанавливается после сбоя хранения, а следующий сеанс завершает тот же cleanup.</summary>
    [Fact]
    public async Task ReceiptStorageAndCleanupFailuresRecoverWithoutAnotherInsert()
    {
        const string begin = """{"version":1,"attemptId":"fixture-remount","kind":"maintenance.complete","status":"running","revision":13,"data":"17","scopes":[{"name":"confirmation","typedRevision":12,"rawRevisions":{}}],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";
        string? durableReceipt = null;
        var attempts = 0;
        await using (var context = CreateContext(out var connection))
        {
            context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true).SetResult(begin);
            context.JSInterop.SetupVoid("clayDraftStore.recordConfirmedOperation", _ => ++attempts == 1)
                .SetException(new JSException("Не удалось сохранить подтверждение"));
            context.JSInterop.SetupVoid("clayDraftStore.recordConfirmedOperation", call =>
            {
                if (attempts != 1 || !Equals(call.Arguments[1], 13L)) return false;
                var receipt = System.Text.Json.Nodes.JsonNode.Parse(begin)!;
                receipt["status"] = "confirmed";
                durableReceipt = receipt.ToJsonString();
                return true;
            }).SetVoidResult();
            context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ => true)
                .SetException(new JSException("Не удалось очистить принятую ревизию"));

            var provider = context.Render<MudDialogProvider>();
            var page = context.Render<MaintenancePage>();
            var click = ClickConfirmation(page);
            await WaitForYes(provider).ClickAsync(new MouseEventArgs());
            await click;
            Assert.Equal(1, connection.InsertCount);
            Assert.Null(durableReceipt);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
            Assert.True(Action(page).Instance.Disabled);

            await RecoveryButton(page, "Перечитать список").ClickAsync(new MouseEventArgs());
            Assert.Equal(1, connection.InsertCount);
            Assert.Equal(13L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
            Assert.All(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"],
                call => Assert.Equal(13L, call.Arguments[1]));
            Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"].Count);
            Assert.NotNull(durableReceipt);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
            Assert.True(Action(page).Instance.Disabled);
        }

        await using var reopened = CreateContext(out var freshConnection, restoredOperation: durableReceipt);
        var reopenedPage = reopened.Render<MaintenancePage>();
        reopenedPage.WaitForAssertion(() => Assert.False(Action(reopenedPage).Instance.Disabled));

        Assert.Equal(0, freshConnection.InsertCount);
        Assert.Empty(reopened.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(reopened.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Equal(13L, Assert.Single(reopened.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
        Assert.False(Action(reopenedPage).Instance.Disabled);
    }

    /// <summary>При смене сотрудника новая личность не может очистить чужой журнал.</summary>
    [Fact]
    public async Task ConfirmedCleanupRequiresCurrentDraftOwner()
    {
        const string receipt = """{"version":1,"attemptId":"employee-owner","kind":"maintenance.complete","status":"confirmed","revision":23,"data":"17","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";
        await using var context = CreateContext(out var connection, restoredOperation: receipt);
        var firstCommit = true;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!firstCommit) return false;
            firstCommit = false;
            return true;
        }).SetException(new JSException("Не удалось записать подтверждение"));
        context.JSInterop.Setup<ClayDraftRestore>("clayDraftStore.register", call => Equals(call.Arguments[2], "8"))
            .SetResult(new ClayDraftRestore("maintenance-other-employee", false, 0, null, null));
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.NotNull(RecoveryButton(page, "Перечитать список")));
        connection.EmployeeId = 8;

        await RecoveryButton(page, "Перечитать список").ClickAsync(new MouseEventArgs());

        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, connection.InsertCount);
    }

    /// <summary>Потеря указателя автоматически восстанавливает единственное подтверждение без INSERT.</summary>
    [Fact]
    public async Task DiscoveredReceiptIsRestoredWithoutAutomaticWrite()
    {
        const string draftId = "deadbeef-1111-2222-3333-444444444444";
        const string receipt = """{"version":1,"attemptId":"last-session","kind":"maintenance.complete","status":"confirmed","revision":9,"data":"17","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";
        await using var context = CreateContext(out var connection, recoveryCandidates: [new(draftId, 9, true, true)]);
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.recoverListedDraft", _ => true)
            .SetResult(new ClayDraftRestore("maintenance-test", true, 9, null, receipt));
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]));
        Assert.DoesNotContain("Восстановить черновик", page.Markup);
        Assert.DoesNotContain("Начать отдельный черновик", page.Markup);

        Assert.Equal(draftId, Assert.Single(context.JSInterop.Invocations["clayDraftStore.recoverListedDraft"]).Arguments[1]);
        Assert.Equal(0, connection.InsertCount);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Даже здоровое хранилище не разрешает отправку при невосстановленном вводе принимаемой области.</summary>
    [Fact]
    public async Task PendingConfirmationScopePreventsBeginAndInsert()
    {
        await using var context = CreateContext(out var connection,
            readiness: new ClayDraftRestoreReadiness(false, ["confirmation"], []));
        var page = context.Render<MaintenancePage>();
        await ClickConfirmation(page);

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, connection.InsertCount);
    }

    /// <summary>Потеря ответа Begin не отправляет INSERT и восстанавливает только свою попытку.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginFailureRetriesStorageWithOnlyProvenUnsentId(bool anotherOperation)
    {
        await using var context = CreateContext(out var connection, failBegin: true);
        if (anotherOperation)
        {
            context.JSInterop.Setup<string?>("clayDraftStore.retryStorage", _ => true)
                .SetResult("""{"version":1,"attemptId":"other-attempt","kind":"maintenance.complete","status":"unknown","revision":3,"data":"17","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        }

        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        var click = ClickConfirmation(page);
        await WaitForYes(provider).ClickAsync(new MouseEventArgs());
        await click;

        Assert.Equal(0, connection.InsertCount);
        Assert.Contains("Команда не отправлена", page.Markup);
        Assert.True(Action(page).Instance.Disabled);
        using var intent = JsonDocument.Parse((string)Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]).Arguments[1]!);
        var attemptId = intent.RootElement.GetProperty("attemptId").GetString();
        Assert.True(Guid.TryParse(attemptId, out _));

        await RecoveryButton(page, "Перечитать список").ClickAsync(new MouseEventArgs());

        Assert.Equal(attemptId, Assert.Single(context.JSInterop.Invocations["clayDraftStore.retryStorage"]).Arguments[1]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Equal(0, connection.InsertCount);
        Assert.Equal(anotherOperation, Action(page).Instance.Disabled);
        if (anotherOperation)
        {
            Assert.Contains("other-attempt", page.Markup);
            Assert.Contains("Работа №17", page.Markup);
        }
    }

    private static AngleSharp.Dom.IElement RecoveryButton(IRenderedComponent<MaintenancePage> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    /// <summary>Выдача и черновик используют последний запрос, даже если предыдущий ещё читает SQL.</summary>
    [Theory]
    [InlineData("B")]
    [InlineData(null)]
    public async Task LatestFilterOrClearWinsWhileReading(string? last)
    {
        await using var context = CreateContext(out var connection);
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.False(Action(page).Instance.Disabled));
        var gate = connection.DeferredListRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ChangeFilterAsync(page, QueryFor("A"));
        try
        {
            await connection.DeferredListStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ChangeFilterAsync(page, QueryFor("obsolete"));
            var accepted = QueryFor(last);
            await ChangeFilterAsync(page, accepted);
            accepted.CompositeFilter?.Nodes.Clear();
        }
        finally { gate.TrySetResult(); }
        await first;

        Assert.Equal(new string?[] { null, "A", last }, connection.ListFilters);
        AssertSavedFilter(context, last);
        Assert.Contains(last is null ? "Работа &lt;test&gt;" : $"Фильтр {last}", page.Markup);
        Assert.DoesNotContain("Фильтр A", page.Markup);
    }

    /// <summary>Фильтр, принятый во время INSERT, применяется после завершения записи.</summary>
    [Fact]
    public async Task FilterSelectedDuringConfirmationIsAppliedAfterWrite()
    {
        await using var context = CreateContext(out var connection);
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        var click = ClickConfirmation(page);
        var gate = connection.InsertGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var yes = WaitForYes(provider).ClickAsync(new MouseEventArgs());
        try
        {
            await connection.InsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ChangeFilterAsync(page, QueryFor("during"));
        }
        finally { gate.TrySetResult(); }
        await Task.WhenAll(click, yes);

        Assert.Equal(1, connection.InsertCount);
        Assert.Equal("during", connection.ListFilters.Last());
        AssertSavedFilter(context, "during");
        Assert.Contains("Фильтр during", page.Markup);
    }

    /// <summary>Освобождение формы отменяет текущий запрос и отбрасывает ожидающий фильтр.</summary>
    [Fact]
    public async Task DisposalDoesNotPublishOrPersistQueuedFilter()
    {
        await using var context = CreateContext(out var connection);
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.False(Action(page).Instance.Disabled));
        var gate = connection.DeferredListRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ChangeFilterAsync(page, QueryFor("A"));
        try
        {
            await connection.DeferredListStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ChangeFilterAsync(page, QueryFor("B"));
            await context.DisposeComponentsAsync();
        }
        finally { gate.TrySetResult(); }
        await first;

        Assert.Equal(new string?[] { null }, connection.ListFilters);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    private static ClayDataQuery QueryFor(string? value) => new()
    {
        CompositeFilter = value is null ? null : new ClayFilterGroupNode
        {
            Nodes = [new ColumnFilter { Column = MaintenanceColumns.Name, Operator = ColumnFilterOperator.Equals, Value = value }],
        },
    };

    private static Task ChangeFilterAsync(IRenderedComponent<MaintenancePage> page, ClayDataQuery query) =>
        page.InvokeAsync(() => page.FindComponent<ClayGrid<MaintenanceWork>>().Instance.OnQueryChanged.InvokeAsync(query));

    private static void AssertSavedFilter(BunitContext context, string? expected)
    {
        var save = Assert.Single(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "InvMaintenanceFilter"));
        var root = JsonSerializer.Deserialize<IClayFilterNode>((string)save.Arguments[2]!);
        if (expected is null)
            Assert.Null(root);
        else
            Assert.Equal(expected, Assert.IsType<ColumnFilter>(Assert.Single(Assert.IsType<ClayFilterGroupNode>(root).Nodes)).Value);
    }

    /// <summary>Срок отображается по смещению на дату записи и попадает в фильтр выбранного дня браузера.</summary>
    [Theory]
    [InlineData(2026, 1, 21)]
    [InlineData(2010, 7, 20)]
    public async Task BrowserDayFiltersSourceWallClockUsingHistoricalOffset(int year, int month, int utcHour)
    {
        var sourceDate = new DateTime(year, month, 2, 0, 30, 0);
        var browserDay = sourceDate.Date.AddDays(-1);
        var expectedUtc = new DateTime(year, month, 1, utcHour, 30, 0, DateTimeKind.Utc);
        await using var context = CreateContext(out var connection, sourceZone: "Russian Standard Time");
        connection.WallClock = sourceDate;
        context.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", _ => true).SetResult(0);
        SetupBrowserMidnight(context, browserDay);
        SetupBrowserMidnight(context, browserDay.AddDays(1));
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.Equal(
            Enumerable.Repeat(expectedUtc.ToString("O"), 2),
            page.FindAll("time").Select(time => time.GetAttribute("datetime"))));

        var query = new ClayDataQuery
        {
            CompositeFilter = new ClayFilterGroupNode
            {
                Nodes = [new ColumnFilter
                {
                    Column = MaintenanceColumns.Deadline,
                    Operator = ColumnFilterOperator.Equals,
                    Value = browserDay,
                }],
            },
        };
        await ChangeFilterAsync(page, query);

        var offset = TimeSpan.FromHours(24 - utcHour);
        Assert.Equal(new[] { browserDay + offset, browserDay.AddDays(1) + offset }, connection.LastDateParameters);
        Assert.Contains("ВыполнитьДо >= @p0 AND ВыполнитьДо < @p1", connection.LastListQuery);
        Assert.Contains("Работа &lt;test&gt;", page.Markup);

        var distinct = await page.FindComponent<ClayGrid<MaintenanceWork>>().Instance.DistinctValuesLoader!(
            MaintenanceColumns.LastCompleted, new ClayDataQuery(), 20);
        var value = Assert.IsType<string>(Assert.Single(distinct.Values));
        Assert.Contains(expectedUtc.ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), value);
        await ChangeFilterAsync(page, new ClayDataQuery
        {
            CompositeFilter = new ClayFilterGroupNode
            {
                Nodes = [new ValueFilter { Column = MaintenanceColumns.LastCompleted, Values = [value] }],
            },
        });
        Assert.Equal(new[] { sourceDate }, connection.LastDateParameters);
        Assert.Contains("ПоследнееВыполнение IN (@p0)", connection.LastListQuery);
        Assert.Contains("Работа &lt;test&gt;", page.Markup);
    }

    /// <summary>Без часового пояса форма показывает работы, а дата не получает предполагаемого смещения.</summary>
    [Fact]
    public async Task MissingSourceZoneKeepsListAndDisablesDateFilters()
    {
        await using var context = CreateContext(out var connection);
        connection.WallClock = new DateTime(2026, 1, 2, 0, 30, 0);
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.False(Action(page).Instance.Disabled));
        Assert.Empty(page.FindAll("time"));
        Assert.Contains("часовой пояс источника", page.Markup);
        Assert.False(page.FindComponents<ClayColumnDef>().Single(column => column.Instance.SqlName == MaintenanceColumns.Deadline).Instance.Filterable);
    }

    private static void SetupBrowserMidnight(BunitContext context, DateTime date)
    {
        var milliseconds = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        context.JSInterop.Setup<long>("clayGridTimeZone.localDateTimeToUtcMilliseconds",
            call => Equals(call.Arguments[0], date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) &&
                Equals(call.Arguments[1], "00:00:00"))
            .SetResult(milliseconds);
    }

    /// <summary>Сортировка заголовком формирует SQL ORDER BY по разрешённой колонке.</summary>
    [Fact]
    public async Task HeaderSortUsesSqlOrderBy()
    {
        await using var context = CreateContext(out var connection);
        context.Render<MudDialogProvider>();
        var page = context.Render<MaintenancePage>();
        page.WaitForAssertion(() => Assert.Contains("Работа <test>", page.Markup.Replace("&lt;", "<").Replace("&gt;", ">")));
        var header = page.FindAll(".clay-grid-column-drag-handle")
            .Single(x => x.TextContent.Contains("Регламентная работа", StringComparison.Ordinal));

        await header.ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Assert.Contains("ORDER BY РегламентнаяРабота ASC", connection.LastListQuery));
        header = page.FindAll(".clay-grid-column-drag-handle")
            .Single(x => x.TextContent.Contains("Регламентная работа", StringComparison.Ordinal));
        await header.ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Assert.Contains("ORDER BY РегламентнаяРабота DESC", connection.LastListQuery));
        Assert.Contains("GETUTCDATE()", connection.LastListQuery);
        Assert.DoesNotContain("DATEDIFF(MINUTE, GETDATE(), GETUTCDATE())", connection.LastListQuery);
    }

    private static Task ClickConfirmation(IRenderedComponent<MaintenancePage> page)
    {
        page.WaitForAssertion(() => Assert.False(Action(page).Instance.Disabled));
        return page.InvokeAsync(() => Action(page).Instance.OnClick.InvokeAsync());
    }

    private static IRenderedComponent<ClayButton> Action(IRenderedComponent<MaintenancePage> page) =>
        page.FindComponents<ClayButton>().Single(button => button.Instance.Text == "Подтвердить выполнение");

    private static AngleSharp.Dom.IElement WaitForYes(IRenderedComponent<MudDialogProvider> provider)
    {
        provider.WaitForAssertion(() => Assert.Contains(provider.FindAll("button"), x => x.TextContent.Trim() == "Да"));
        return provider.FindAll("button").Single(x => x.TextContent.Trim() == "Да");
    }

    private static BunitContext CreateContext(out MaintenanceConnection connection, bool failBegin = false, string? sourceZone = null, string? restoredOperation = null, ClayDraftRecoveryCandidate[]? recoveryCandidates = null, ClayDraftRestoreReadiness? readiness = null,
        int? restoredConfirmation = null, NavigationManager? navigation = null)
    {
        var db = connection = new MaintenanceConnection();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("maintenance-test", restoredOperation is not null || restoredConfirmation is not null,
                0, null, restoredOperation, restoredConfirmation is { } id
                    ? new Dictionary<string, string> { ["confirmation"] = JsonSerializer.Serialize(id) } : null));
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true)
            .SetResult(recoveryCandidates ?? []);
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(readiness ?? new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        var begin = context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true);
        if (failBegin) begin.SetException(new JSException("Begin не подтверждён"));
        else begin
            .SetResult("""{"version":1,"attemptId":"maintenance-test","kind":"maintenance.complete","status":"running","revision":2,"data":"17","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.ComponentFactories.AddStub<InventoryMainMenu>();
        DapperColumnMapper.Initialize();
        context.Services.AddScoped(_ => new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => db));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddSingleton(new Clayzor.Lib.Web.Settings.ClayAppSettings());
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddSingleton<AuthenticationStateProvider, MaintenanceAuthentication>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Maintenance:SourceTimeZoneId"] = sourceZone })
            .Build());
        if (navigation is not null) context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Render<MudPopoverProvider>();
        return context;
    }

    private sealed class MaintenanceAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class MaintenanceConnection : DbConnection
    {
        public bool HasRole { get; set; } = true;
        public int EmployeeId { get; set; } = 4;
        public bool WorkAvailable { get; set; } = true;
        public bool FailReadAfterInsert { get; set; }
        public bool HidePendingWork { get; set; }
        public bool FailListRead { get; set; }
        public bool FailInsert { get; set; }
        public int InsertCount { get; private set; }
        public int? CompletedId { get; private set; }
        public string LastListQuery { get; private set; } = "";
        public List<string?> ListFilters { get; } = [];
        public DateTime? WallClock { get; set; }
        public DateTime[] LastDateParameters { get; private set; } = [];
        public TaskCompletionSource? DeferredListRead { get; set; }
        public TaskCompletionSource DeferredListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? InsertGate { get; set; }
        public TaskCompletionSource InsertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new Command(this);

        private sealed class Command(MaintenanceConnection owner) : DbCommand
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

            public override async Task<int> ExecuteNonQueryAsync(CancellationToken ct)
            {
                Assert.Contains("INSERT INTO dbo.РегламентныеРаботыВыполнение", CommandText);
                var parameter = Assert.Single(Parameters.Cast<DbParameter>());
                Assert.Equal("id", parameter.ParameterName);
                owner.CompletedId = (int)parameter.Value!;
                owner.InsertCount++;
                owner.InsertStarted.TrySetResult();
                if (owner.InsertGate is { } gate) await gate.Task.WaitAsync(ct);
                if (owner.FailInsert) throw new TimeoutException();
                return 1;
            }

            protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct)
            {
                if (CommandText.Contains("SELECT GETUTCDATE()") && owner.DeferredListRead is { } gate)
                {
                    owner.DeferredListRead = null;
                    owner.DeferredListStarted.TrySetResult();
                    await gate.Task.WaitAsync(ct);
                }
                return ExecuteDbDataReader(behavior);
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (CommandText.Contains("SUSER_SID()"))
                    return Table(("КодСотрудника", owner.EmployeeId)).CreateDataReader();
                if (CommandText.Contains("fn_ТекущиеРоли"))
                {
                    var roles = Table(("RoleId", (int)Роли.Дежурный_инженер), ("PersonId", 0));
                    if (!owner.HasRole) roles.Rows.Clear();
                    return roles.CreateDataReader();
                }
                if (CommandText.Contains("HAS_PERMS_BY_NAME"))
                    return Table(("Value", true)).CreateDataReader();
                if (CommandText.Contains("WHERE КодРегламентныхРабот = @id"))
                    return Table(("Value", owner.WorkAvailable)).CreateDataReader();
                Assert.Contains("FROM dbo.vwРегламентныеРаботы", CommandText);
                if (CommandText.Contains("SELECT DISTINCT TOP") && owner.WallClock is { } distinct)
                    return Table(("Value", distinct)).CreateDataReader();
                owner.LastListQuery = CommandText;
                owner.LastDateParameters = Parameters.Cast<DbParameter>().Select(parameter => parameter.Value)
                    .OfType<DateTime>().ToArray();
                var filter = Parameters.Cast<DbParameter>().FirstOrDefault(parameter => parameter.ParameterName == "p0")?.Value as string;
                owner.ListFilters.Add(filter);
                if (owner.FailListRead || owner.FailReadAfterInsert && owner.InsertCount > 0) throw new InvalidOperationException("test read failure");
                var rows = Table(("Id", 17), ("Name", filter is null ? "Работа <test>" : $"Фильтр {filter}"),
                    ("Responsible", "Дежурный"), ("Instruction", ""));
                if (owner.WallClock is { } wallClock)
                {
                    rows.Columns.Add("Deadline", typeof(DateTime));
                    rows.Columns.Add("LastCompleted", typeof(DateTime));
                    rows.Rows[0]["Deadline"] = wallClock;
                    rows.Rows[0]["LastCompleted"] = wallClock;
                    if (owner.LastDateParameters is [var low, var high] && (wallClock < low || wallClock >= high) ||
                        owner.LastDateParameters is [var exact] && wallClock != exact)
                        rows.Rows.Clear();
                }
                if (owner.HidePendingWork) rows.Rows.Clear();
                return new DataTableReader([Table(("Now", new DateTime(2026, 11, 25, 15, 0, 0))), rows]);
            }

            private static DataTable Table(params (string Name, object Value)[] values)
            {
                var table = new DataTable();
                foreach (var (name, value) in values) table.Columns.Add(name, value.GetType());
                table.Rows.Add(values.Select(value => value.Value).ToArray());
                return table;
            }
        }
    }
}

