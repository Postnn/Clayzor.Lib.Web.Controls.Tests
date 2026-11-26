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
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Pages.Maintenance;
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
public sealed class MaintenanceTests
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
        await RecoveryButton(page, "Завершить обработку черновика").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, connection.InsertCount);
        Assert.False(Action(page).Instance.Disabled);
        var commits = context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        Assert.Equal(2, commits.Count);
        Assert.All(commits, commit => Assert.Equal(2L, commit.Arguments[1]));
        Assert.DoesNotContain("Не удалось завершить", page.Markup);
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

        await RecoveryButton(page, "Повторить сохранение черновика").ClickAsync(new MouseEventArgs());

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
        Assert.Contains("DATEDIFF(MINUTE, GETDATE(), GETUTCDATE())", connection.LastListQuery);
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

    private static BunitContext CreateContext(out MaintenanceConnection connection, bool failBegin = false)
    {
        var db = connection = new MaintenanceConnection();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("maintenance-test", false, 0, null, null));
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
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
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
        public bool WorkAvailable { get; set; } = true;
        public bool FailReadAfterInsert { get; set; }
        public bool FailInsert { get; set; }
        public int InsertCount { get; private set; }
        public int? CompletedId { get; private set; }
        public string LastListQuery { get; private set; } = "";
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

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (CommandText.Contains("SUSER_SID()"))
                    return Table(("КодСотрудника", 4)).CreateDataReader();
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
                owner.LastListQuery = CommandText;
                if (owner.FailReadAfterInsert && owner.InsertCount > 0) throw new InvalidOperationException("test read failure");
                return new DataTableReader([
                    Table(("Now", new DateTime(2026, 11, 25, 15, 0, 0))),
                    Table(("Id", 17), ("Name", "Работа <test>"), ("Responsible", "Дежурный"), ("Instruction", "")),
                ]);
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

