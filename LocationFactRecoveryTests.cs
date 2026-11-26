using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Pages.Inv;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Восстановление операции через рабочую форму инвентаризации.</summary>
public sealed class LocationFactRecoveryTests
{
    private const string Notes = "Проверено в fixture";
    private static readonly LocationFactSnapshot Snapshot = new(new(10, "Тестовый склад", true),
        new(17, 4, "Владелец", "Тестовое устройство", "", 10, "Тестовый склад", null, new DateTime(2026, 11, 20)),
        null, false, "");

    /// <summary>После подтверждения БД повтор браузерного commit использует ту же ревизию без INSERT.</summary>
    [Fact]
    public async Task ConfirmedFactRetriesOnlyCleanupAndUnblocksNextEquipment()
    {
        using var db = new FactConnection();
        await using var context = Configure(db);
        var first = true;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!first) return false;
            first = false;
            return true;
        }).SetException(new JSException("Тестовый отказ browser cleanup"));
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await WaitForButton(page, dialogs, "Подтвердить").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, db.Inserts);
        Assert.Equal(1, db.Commits);
        Assert.Equal(Notes, db.SentNotes);
        Assert.Equal(1, db.SentBroken);
        Assert.Contains("Проверка записана", page.Markup);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var wire = JsonDocument.Parse((string)begin.Arguments[1]!);
        using var intent = JsonDocument.Parse(wire.RootElement.GetProperty("data").GetString()!);
        Assert.Equal(Notes, intent.RootElement.GetProperty("Notes").GetString());
        Assert.True(intent.RootElement.GetProperty("Broken").GetBoolean());
        Assert.False(intent.RootElement.GetProperty("Move").GetBoolean());
        Assert.Equal(17, intent.RootElement.GetProperty("Expected").GetProperty("Equipment").GetProperty("EquipmentId").GetInt32());
        await WaitForButton(page, dialogs, "Завершить обработку черновика").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, db.Inserts);
        var commits = context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        Assert.Equal(2, commits.Count);
        Assert.All(commits, call => Assert.Equal(6L, call.Arguments[1]));
        Assert.False(WaitForButton(page, dialogs, "Тестовое устройство").HasAttribute("disabled"));
    }

    /// <summary>Ошибка ответа Begin не запускает SQL, повтор исправляет только известную неотправленную попытку.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostBeginAcknowledgementRepairsStorageWithoutFacts(bool anotherAttempt)
    {
        using var db = new FactConnection();
        await using var context = Configure(db, failBegin: true);
        if (anotherAttempt)
            context.JSInterop.Setup<string?>("clayDraftStore.retryStorage", _ => true)
                .SetResult("""{"version":1,"attemptId":"another-fact","kind":"confirm-location","status":"unknown","revision":8,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await WaitForButton(page, dialogs, "Подтвердить").ClickAsync(new MouseEventArgs());
        Assert.Contains("Команда не отправлена", page.Markup);
        Assert.Equal(0, db.Inserts);

        var invocation = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var wire = JsonDocument.Parse((string)invocation.Arguments[1]!);
        var unsent = wire.RootElement.GetProperty("attemptId").GetString();
        Assert.True(Guid.TryParse(unsent, out _));
        await WaitForButton(page, dialogs, "Повторить сохранение черновика").ClickAsync(new MouseEventArgs());
        Assert.Equal(unsent, Assert.Single(context.JSInterop.Invocations["clayDraftStore.retryStorage"]).Arguments[1]);
        Assert.Equal(0, db.Inserts);
        Assert.Equal(anotherAttempt, WaitForButton(page, dialogs, "Подтвердить").HasAttribute("disabled"));
        if (anotherAttempt) Assert.Contains("another-fact", page.Markup);
    }

    /// <summary>Потеря ответа после INSERT допускает сверку и сохраняет блокировку повтора.</summary>
    [Fact]
    public async Task TrueUnknownAllowsReadOnlyComparisonWithoutSecondFact()
    {
        using var db = new FactConnection { LoseInsertResponse = true };
        await using var context = Configure(db);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await WaitForButton(page, dialogs, "Подтвердить").ClickAsync(new MouseEventArgs());

        Assert.Contains("fact-test-attempt", page.Markup);
        Assert.Equal(1, db.Inserts);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        var checkbox = BrokenControl(page, dialogs);
        var notes = NotesControl(page, dialogs);
        Assert.True(checkbox.Disabled);
        Assert.True(notes.Options!.ReadOnly);
        var before = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count;
        await page.InvokeAsync(() => checkbox.StateChanged.InvokeAsync(false));
        await page.InvokeAsync(() => notes.ValueChanged.InvokeAsync("Поздний ввод"));
        Assert.True(BrokenControl(page, dialogs).State);
        Assert.Equal(Notes, NotesControl(page, dialogs).Value);
        Assert.Equal(before, context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count);
        await WaitForButton(page, dialogs, "Сверить с базой").ClickAsync(new MouseEventArgs());
        Assert.True(WaitForButton(page, dialogs, "Подтвердить").HasAttribute("disabled"));
        Assert.Equal(1, db.Inserts);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
    }

    /// <summary>Запоздавшие события предыдущего окна не меняют следующий черновик после отмены.</summary>
    [Fact]
    public async Task LateCallbacksCannotChangeNextEditorAfterCancel()
    {
        using var db = new FactConnection();
        await using var context = Configure(db);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        WaitForButton(page, dialogs, "Подтвердить");
        var oldBroken = BrokenControl(page, dialogs).StateChanged;
        var oldNotes = NotesControl(page, dialogs).ValueChanged;

        await WaitForButton(page, dialogs, "Отмена").ClickAsync(new MouseEventArgs());
        await WaitForButton(page, dialogs, "Тестовое устройство").ClickAsync(new MouseEventArgs());
        WaitForButton(page, dialogs, "Подтвердить");
        var before = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count;

        await page.InvokeAsync(() => oldBroken.InvokeAsync(true));
        await page.InvokeAsync(() => oldNotes.InvokeAsync("Поздний ввод"));

        Assert.Equal(before, context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count);
        Assert.False(BrokenControl(page, dialogs).State);
        Assert.Equal("", NotesControl(page, dialogs).Value);

        await page.InvokeAsync(() => BrokenControl(page, dialogs).StateChanged.InvokeAsync(true));
        await page.InvokeAsync(() => NotesControl(page, dialogs).ValueChanged.InvokeAsync(Notes));
        Assert.True(BrokenControl(page, dialogs).State);
        Assert.Equal(Notes, NotesControl(page, dialogs).Value);
        var call = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last();
        using var draft = JsonDocument.Parse((string)call.Arguments[2]!);
        Assert.True(draft.RootElement.GetProperty("Broken").GetBoolean());
        Assert.Equal(Notes, draft.RootElement.GetProperty("Notes").GetString());
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Контекст рабочего места переживает отмену. Временный null и явная очистка различаются.</summary>
    [Fact]
    public async Task WorkplaceContextSurvivesCancelAndChangesOnlyOnExplicitClear()
    {
        using var db = new FactConnection();
        var workplace = JsonSerializer.Serialize(new { SiteId = 10, ResponsibleId = 4, ItOnly = true, Scanner = true });
        await using var context = Configure(db, workplaceJson: workplace);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        WaitForButton(page, dialogs, "Подтвердить");

        Assert.True(page.FindComponents<ClayCheckbox>().Single(c => c.Instance.Label == "IT-оборудование").Instance.State);
        Assert.True(page.FindComponents<ClayCheckbox>().Single(c => c.Instance.Label == "Режим ввода - сканер").Instance.State);
        Assert.Equal(4, db.ResponsibleOnLastList);
        await WaitForButton(page, dialogs, "Отмена").ClickAsync(new MouseEventArgs());
        var discard = Assert.Single(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.Equal("main", discard.Arguments[1]);

        var location = page.FindComponent<ClaySelect<LocationLookup, int?>>();
        await page.InvokeAsync(() => location.Instance.ValueChanged.InvokeAsync((int?)null));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Where(call => Equals(call.Arguments[1], "workplace")));
        Assert.False(WaitForButton(page, dialogs, "Тестовое устройство").HasAttribute("disabled"));

        await page.InvokeAsync(() => location.Instance.Cleared.InvokeAsync());
        var saved = Assert.Single(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "workplace"));
        using var snapshot = JsonDocument.Parse((string)saved.Arguments[2]!);
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("SiteId").ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("ResponsibleId").ValueKind);
        Assert.True(snapshot.RootElement.GetProperty("ItOnly").GetBoolean());
        Assert.True(snapshot.RootElement.GetProperty("Scanner").GetBoolean());
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Сырой поиск без FactDraft ожидает доступного поля и восстанавливается без открытия подтверждения.</summary>
    [Fact]
    public async Task RawOnlyWorkplaceWaitsForSearchFieldWithoutDispatch()
    {
        using var db = new FactConnection();
        var searchReady = false;
        await using var context = Configure(db, includeEditor: false, searchReady: () => searchReady);
        var page = context.Render<LocationFact>();
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"],
            call => Equals(call.Arguments[1], "workplace")));
        Assert.Contains("Повторить восстановление поиска", page.Markup);

        searchReady = true;
        var location = page.FindComponent<ClaySelect<LocationLookup, int?>>();
        await page.InvokeAsync(() => location.Instance.ValueChanged.InvokeAsync((int?)10));
        page.WaitForAssertion(() => Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count(
            call => Equals(call.Arguments[1], "workplace"))));
        Assert.DoesNotContain("Повторить восстановление поиска", page.Markup);

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.openScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Старый raw без снимка оборудования сохраняется, а новый факт блокируется до миграции.</summary>
    [Fact]
    public async Task LegacyOrphanRawRemainsProtected()
    {
        using var db = new FactConnection();
        await using var context = Configure(db, includeEditor: false, legacyRaw: true);
        var page = context.Render<LocationFact>();
        page.WaitForAssertion(() => Assert.Contains("Черновик прежней версии", page.Markup));

        var location = page.FindComponent<ClaySelect<LocationLookup, int?>>();
        await page.InvokeAsync(() => location.Instance.ValueChanged.InvokeAsync((int?)10));
        Assert.Contains("Черновик прежней версии", page.Markup);
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Trim() == "Тестовое устройство");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.Equal(0, db.Inserts);
    }

    private static ClayCheckbox BrokenControl(IRenderedComponent<LocationFact> page, IRenderedComponent<MudDialogProvider> dialogs) =>
        page.FindComponents<ClayCheckbox>().Concat(dialogs.FindComponents<ClayCheckbox>())
            .First(component => component.Instance.Label == "Сломано, ремонту не подлежит").Instance;

    private static ClayTextArea NotesControl(IRenderedComponent<LocationFact> page, IRenderedComponent<MudDialogProvider> dialogs) =>
        page.FindComponents<ClayTextArea>().Concat(dialogs.FindComponents<ClayTextArea>())
            .First(component => component.Instance.Options?.Label == "Примечание").Instance;

    private static AngleSharp.Dom.IElement WaitForButton(IRenderedComponent<LocationFact> page,
        IRenderedComponent<MudDialogProvider> dialogs, string text)
    {
        IEnumerable<AngleSharp.Dom.IElement> Matching() => page.FindAll("button").Concat(dialogs.FindAll("button"))
            .Where(button => button.TextContent.Trim() == text);
        page.WaitForAssertion(() => Assert.NotEmpty(Matching()));
        return Matching().First();
    }

    private static BunitContext Configure(FactConnection connection, bool failBegin = false,
        bool includeEditor = true, string? workplaceJson = null, Func<bool>? searchReady = null, bool legacyRaw = false)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var restored = JsonSerializer.Serialize(new { Snapshot, Notes, Broken = true, Scanner = false, Conflict = false });
        var scopes = new Dictionary<string, string>();
        if (includeEditor) scopes["main"] = restored;
        if (workplaceJson is not null) scopes["workplace"] = workplaceJson;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("fact-test", true, 5, null, null, scopes));
        bool SearchPending() => searchReady?.Invoke() == false;
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", call => SearchPending() && Equals(call.Arguments[1], "workplace"))
            .SetResult(new ClayDraftRawApplyResult([], ["data:fact.search"]));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", call => !SearchPending() || !Equals(call.Arguments[1], "workplace"))
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => SearchPending())
            .SetResult(new ClayDraftRestoreReadiness(false, ["workplace"], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => !SearchPending())
            .SetResult(new ClayDraftRestoreReadiness(!legacyRaw, legacyRaw ? ["main"] : [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        var begin = context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true);
        if (failBegin) begin.SetException(new JSException("Тестовая потеря ответа Begin"));
        else begin.SetResult("""{"version":1,"attemptId":"fact-test-attempt","kind":"confirm-location","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.ComponentFactories.AddStub<InventoryMainMenu>();
        DapperColumnMapper.Initialize();
        context.Services.AddScoped(_ => new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddSingleton(new Clayzor.Lib.Web.Settings.ClayAppSettings());
        context.Services.AddScoped<LocationSelectDataSource>();
        context.Services.AddScoped<EmployeeSelectDataSource>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
        context.Services.AddMudServices();
        context.Render<MudPopoverProvider>();
        return context;
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class FactConnection : DbConnection
    {
        internal int Inserts { get; private set; }
        internal bool LoseInsertResponse { get; init; }
        internal int Commits { get; private set; }
        internal string? SentNotes { get; private set; }
        internal int SentBroken { get; private set; }
        internal int? ResponsibleOnLastList { get; private set; }
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => new Transaction(this, level);
        protected override DbCommand CreateDbCommand() => new Command(this);

        private sealed class Transaction(FactConnection owner, IsolationLevel level) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => level;
            protected override DbConnection DbConnection => owner;
            public override void Commit() => owner.Commits++;
            public override void Rollback() { }
        }

        private sealed class Command(FactConnection owner) : DbCommand
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
                if (CommandText.Contains("SUSER_SID()")) return Row(("КодСотрудника", 4));
                if (CommandText.Contains("@RoleId")) return Row(("Value", 1));
                if (CommandText.Contains("fn_ТекущиеРоли"))
                    return Row(("RoleId", (int)LocationFactData.RequiredRole), ("PersonId", 0));
                if (CommandText.Contains("AS CanConfirm"))
                    return Row(("NowUtc", new DateTime(2026, 11, 26, 14, 0, 0)), ("CanConfirm", true), ("CanMove", true));
                if (CommandText.StartsWith("INSERT dbo.vwРеальноеРасположение", StringComparison.Ordinal))
                {
                    Assert.Equal(IsolationLevel.Serializable, DbTransaction!.IsolationLevel);
                    owner.Inserts++;
                    owner.SentNotes = (string)Param("Notes")!;
                    owner.SentBroken = (int)Param("Broken")!;
                    if (owner.LoseInsertResponse) throw SqlExceptionFactory.Create(-2, "Тестовая потеря ответа INSERT");
                    return Row(("Count", 1));
                }
                if (CommandText.Contains("WITH Booked AS"))
                {
                    owner.ResponsibleOnLastList = Param("ResponsibleId") is int responsible ? responsible : null;
                    return Row(("Id", 17), ("Name", Snapshot.Equipment.FactName), ("CheckedAt", (DateTime?)null),
                        ("LocationId", 10), ("LocationName", Snapshot.Site.Name), ("Broken", false), ("Notes", ""));
                }
                if (CommandText.Contains("КодСотрудника") && CommandText.Contains("@Id"))
                    return Row(("КодСотрудника", 4), ("Сотрудник", "Проверяющий"));
                if (CommandText.Contains("AS BookedLocationName"))
                    return Row(("EquipmentId", 17), ("OwnerId", 4), ("OwnerName", Snapshot.Equipment.OwnerName),
                        ("FactName", Snapshot.Equipment.FactName), ("InventoryNumber", ""), ("BookedLocationId", 10),
                        ("BookedLocationName", Snapshot.Site.Name), ("BookedFromUtc", (DateTime?)null),
                        ("EquipmentChangedAt", Snapshot.Equipment.EquipmentChangedAt));
                if (CommandText.Contains("РеальноеРасположениеОборудования")) return EmptyRows();
                if (CommandText.Contains("Расположения"))
                    return Row(("Id", 10), ("Name", Snapshot.Site.Name), ("IsWarehouse", true));
                throw new InvalidOperationException("Неожиданное чтение fixture инвентаризации.");
            }

            private object? Param(string name) => Parameters.Cast<DbParameter>().Single(p => p.ParameterName == name).Value;

            private static DbDataReader EmptyRows()
            {
                var table = new DataTable();
                table.Columns.Add("CheckedAt", typeof(DateTime));
                return table.CreateDataReader();
            }

            private static DbDataReader Row(params (string Name, object? Value)[] values)
            {
                var table = new DataTable();
                foreach (var (name, value) in values) table.Columns.Add(name, value?.GetType() ?? typeof(DateTime));
                table.Rows.Add(values.Select(value => value.Value ?? DBNull.Value).ToArray());
                return table.CreateDataReader();
            }
        }
    }
}
