using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверка трёх решений на реальной карточке территории.</summary>
public sealed class TerritorialEntityCancelTests
{
    /// <summary>Свежее чтение и проверка ревизии предшествуют принятию сброса.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ResetReadsFirstAndKeepsDraftOnMissingOrNewInput(bool missing, bool accepted)
    {
        await using var fixture = new Fixture(missing, accepted);
        var opening = Button(fixture.Card, "Отмена").ClickAsync(new MouseEventArgs());
        fixture.Dialogs.WaitForAssertion(() => Assert.Contains("Несохранённые изменения", fixture.Dialogs.Markup));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        await Button(fixture.Dialogs, "Отменить мои изменения и загрузить из БД").ClickAsync(new MouseEventArgs());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));
        if (missing)
            Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        else
        {
            var commit = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal(7L, commit.Arguments[2]);
            using var saved = JsonDocument.Parse(Assert.IsType<string>(commit.Arguments[3]));
            Assert.Equal("FreshName", saved.RootElement.GetProperty("Original").GetProperty("Record").GetProperty("Name").GetString());
            Assert.Equal("FreshName", saved.RootElement.GetProperty("Current").GetProperty("Record").GetProperty("Name").GetString());
        }
        Assert.Contains(missing || !accepted ? "LocalName" : "FreshName", fixture.Card.Markup);
        Assert.Equal(missing || !accepted ? 1 : 0, fixture.Card.FindAll("[data-clay-field-changed]").Count);
    }

    /// <summary>Продолжение работы не перечитывает БД, закрытие удаляет только принятую RAM-ревизию.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueAndCloseDoNotReadTheDatabaseAgain(bool close)
    {
        await using var fixture = new Fixture();
        var before = fixture.Connection.CommandLog.Count;
        var opening = Button(fixture.Card, "Отмена").ClickAsync(new MouseEventArgs());
        fixture.Dialogs.WaitForAssertion(() => Assert.Contains("Несохранённые изменения", fixture.Dialogs.Markup));
        await Button(fixture.Dialogs, close ? "Закрыть без сохранения" : "Продолжить редактирование").ClickAsync(new MouseEventArgs());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(before, fixture.Connection.CommandLog.Count);
        var replacing = fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        if (!close)
        {
            Assert.Empty(replacing);
            Assert.Contains("LocalName", fixture.Card.Markup);
            return;
        }

        Assert.Empty(replacing);
        var discard = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Equal("territory-cancel", discard.Arguments[0]);
        Assert.Equal(7L, discard.Arguments[1]);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }

    /// <summary>Форма показывает изменение сохранённого черновика, возврат снимает рамку.</summary>
    [Fact]
    public async Task NameTracksAcceptedDraftBaseline()
    {
        await using var fixture = new Fixture();
        const string nameInput = "input[data-draft-key='territory.name']";
        var marked = Assert.Single(fixture.Card.FindAll("[data-clay-field-changed]"));
        Assert.NotNull(marked.QuerySelector($".clay-required-editable {nameInput}"));
        var description = marked.GetAttribute("aria-describedby");
        Assert.Equal("Есть несохранённое изменение", marked.QuerySelector($"#{description}")?.TextContent);

        await fixture.Card.Find(nameInput).InputAsync("OldName");
        Assert.Empty(fixture.Card.FindAll("[data-clay-field-changed]"));
        await fixture.Card.Find(nameInput).InputAsync("AnotherName");
        Assert.Single(fixture.Card.FindAll("[data-clay-field-changed]"));
        Assert.Contains("AnotherName", fixture.Card.Markup);
    }

    /// <summary>Отмена сверки разных значений сохраняет прежнее исходное значение.</summary>
    [Fact]
    public async Task RefreshWithoutAcceptingMergePreservesDirtyName()
    {
        await using var fixture = new Fixture();
        var refresh = Button(fixture.Card, "Обновить").ClickAsync(new MouseEventArgs());
        fixture.Dialogs.WaitForAssertion(() => Assert.Contains("Для каждого поля выберите значение", fixture.Dialogs.Markup));
        await Button(fixture.Dialogs, "Отмена").ClickAsync(new MouseEventArgs());
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(fixture.Card.FindAll("[data-clay-field-changed]"));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
    }

    /// <summary>Изменение либо удаление записи после открытия оставляет пользовательский ввод.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedCompareAndSwapReadsCurrentRecordAndKeepsLocalInput(bool missing)
    {
        await using var fixture = new Fixture(missing: missing, simulateWriteConflict: true);
        var apply = Button(fixture.Card, "Применить");
        Assert.False(apply.HasAttribute("disabled"));

        await apply.ClickAsync(new MouseEventArgs());

        var message = missing
            ? "Запись недоступна для сверки. Ваш ввод сохранён."
            : "Карточка изменилась. Нажмите";
        Assert.Contains(message, fixture.Card.Markup);
        Assert.Contains("LocalName", fixture.Card.Markup);
        Assert.Single(fixture.Card.FindAll("[data-clay-field-changed]"));
        Assert.True(Button(fixture.Card, "Применить").HasAttribute("disabled"));
        Assert.False(Button(fixture.Card, "Обновить").HasAttribute("disabled"));
        var commands = fixture.Connection.CommandLog;
        Assert.Equal(3, commands.Count(sql => sql.Contains("FROM dbo.Территории t ", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, sql => sql == TerritorialEntitySql.TerritoryUpdate);
        Assert.Single(commands, sql => sql == SQLQueries.UPDATE_ClayzorOperationScope);
        var transition = commands.IndexOf(SQLQueries.UPDATE_ClayzorOperationJournalState);
        Assert.True(transition >= 0);
        Assert.Equal((byte)InventoryOperationState.Rejected, fixture.Connection.ParameterLog[transition]["NextState"]);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
    }

    /// <summary>Повтор после сбоя отметки подтверждения завершает журнал обычного обновления.</summary>
    [Fact]
    public async Task KnownTerritoryUpdateRetriesMarkerWithoutRepeatingWrite()
    {
        await using var fixture = new Fixture(simulateWriteSuccess: true);
        var failRecording = true;
        fixture.Context.JSInterop.SetupVoid("clayDraftStore.recordConfirmedOperation", _ => failRecording)
            .SetException(new JSException("Тестовый отказ записи подтверждения."));

        await Button(fixture.Card, "Применить").ClickAsync(new MouseEventArgs());

        Assert.Single(fixture.Connection.CommandLog, sql => sql == TerritorialEntitySql.TerritoryUpdate);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        failRecording = false;
        await Button(fixture.Card, "Обновить").ClickAsync(new MouseEventArgs());

        var cleanup = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        var confirmations = fixture.Context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"];
        Assert.Equal(2, confirmations.Count);
        Assert.All(confirmations, call => Assert.Equal(cleanup.Arguments[1], call.Arguments[1]));
        Assert.Single(fixture.Connection.CommandLog, sql => sql == TerritorialEntitySql.TerritoryUpdate);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }


    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> component, string text) where T : IComponent =>
        component.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(bool missing = false, bool acceptsRevision = true, string freshName = "FreshName",
            bool simulateWriteConflict = false, bool simulateWriteSuccess = false)
        {
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            DapperColumnMapper.Initialize();
            var employee = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Test")));
            var access = Script.Rows(Script.Row(("TerritoryUpdate", true)));
            var old = new TerritorialEntityRecord
            {
                Id = 4, Type = ТипыТерриитории.Страны, Name = "OldName", CountryCode = "1", Direction = "Север",
                ChangedAt = new DateTime(2026, 11, 25)
            };
            var fresh = missing ? Script.Rows() : Territory(freshName, old.ChangedAt.AddDays(1));
            var noPending = Script.Rows(Script.Row(("Pending", false)));
            var script = new Queue<ScriptEntry>([
                employee, Script.Rows(), access, Territory("OldName", old.ChangedAt), Script.Rows(), noPending
            ]);
            if (simulateWriteConflict || simulateWriteSuccess)
            {
                foreach (var entry in new[]
                {
                    noPending,                             // Проверка дерева перед Begin.
                    employee, access, Script.Rows(),        // Права и точный результат попытки.
                    Script.Rows(), noPending, noPending,    // Резервирование карточки и дерева.
                    Script.NonQuery(), Script.Scalar(1),
                    employee, access, simulateWriteSuccess ? Territory("OldName", old.ChangedAt) : fresh,
                })
                    script.Enqueue(entry);
                if (simulateWriteSuccess)
                {
                    script.Enqueue(Script.NonQuery());
                    script.Enqueue(Territory("LocalName", old.ChangedAt.AddDays(1)));
                }
                script.Enqueue(Script.Scalar(1));
                script.Enqueue(Script.Scalar(1));
            }
            foreach (var entry in new[] { employee, Script.Rows(), access, fresh, Script.Rows(), noPending })
                script.Enqueue(entry);
            Connection = new ScriptedConnection(script, emulateEquipmentIdentity: false);
            Context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => simulateWriteConflict || simulateWriteSuccess ? new TransactionConnection(Connection, () => { }) : Connection));
            Context.Services.AddScoped(_ => new ClayDraftService(new JournalRuntime(Context.JSInterop.JSRuntime)));
            Context.Services.AddScoped<ClayFormWindowService>();
            Context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            Context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
            Context.Services.AddMudServices();
            var stored = JsonSerializer.Serialize(new
            {
                Original = new TerritorialEntityForm.FormState(old, null),
                Current = new TerritorialEntityForm.FormState(old with { Name = "LocalName" }, null)
            });
            Context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>(
                "clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true).SetResult([]);
            Context.JSInterop.SetupVoid("clayDraftStore.finishLegacyOperationEvidenceTransfer", _ => true).SetVoidResult();
            Context.JSInterop.SetupVoid("clayDraftStore.retireLegacyOperationEvidenceTransfer", _ => true).SetVoidResult();
            Context.JSInterop.Setup<bool>("clayDraftStore.closeRam", call => call.Arguments.Count == 2).SetResult(true);
            Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
                .SetResult(new ClayDraftRestore("territory-cancel", true, 7, null, null,
                    new Dictionary<string, string> { ["main"] = stored }));
            Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], []));
            Context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
                .SetResult(new ClayDraftRestoreReadiness(true, [], []));
            Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
                .SetResult(new ClayDraftEditorScopeState(7, true, false));
            Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(acceptsRevision);
            Context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
                .SetResult(new ClayDraftRebaseState(7, []));
            Context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
                .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 7, 7));
            Context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
                .SetResult("""{"version":1,"attemptId":"territory-conflict","kind":"territory.update","status":"running","revision":8,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
            Dialogs = Context.Render<MudDialogProvider>();
            Context.Render<MudPopoverProvider>();
            Context.Services.GetRequiredService<NavigationManager>().NavigateTo("/TerritorialEntity/TerritorialEntity_form?id=4");
            Card = Context.Render<TerritorialEntityForm>();
            Card.WaitForAssertion(() => Assert.Contains("LocalName", Card.Markup));
        }

        internal BunitContext Context { get; } = new();
        internal ScriptedConnection Connection { get; }
        internal IRenderedComponent<MudDialogProvider> Dialogs { get; }
        internal IRenderedComponent<TerritorialEntityForm> Card { get; }

        private static ScriptEntry Territory(string name, DateTime changed) =>
            Script.Rows(Script.Row(("Id", 4), ("Type", (int)ТипыТерриитории.Страны), ("Name", name),
                ("CountryCode", "1"), ("Direction", "Север"), ("ChangedAt", changed)));

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class JournalRuntime(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args)
        {
            var result = await inner.InvokeAsync<T>(identifier, ct, args);
            if (identifier != "clayDraftStore.beginOperationIntent")
                return result;

            using var wire = JsonDocument.Parse((string)args![1]!);
            var intent = wire.RootElement;
            var attemptId = intent.GetProperty("attemptId").GetString();
            Assert.True(Guid.TryParseExact(attemptId, "N", out _));
            return (T)(object)JsonSerializer.Serialize(new
            {
                version = 1,
                attemptId,
                kind = intent.GetProperty("kind").GetString(),
                status = "running",
                revision = 8,
                data = intent.GetProperty("data").GetString(),
                scopes = new[] { new { name = "main", typedRevision = 7, rawRevisions = new Dictionary<string, long>() } },
                acceptLegacyTyped = false,
                legacyTypedRevision = 0,
            });
        }
    }

    internal sealed class TransactionConnection(ScriptedConnection script, Action? commit = null) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => script.ConnectionString; set => script.ConnectionString = value ?? ""; }
        public override string Database => script.Database;
        public override string DataSource => script.DataSource;
        public override string ServerVersion => script.ServerVersion;
        public override ConnectionState State => script.State;
        public override void Open() => script.Open();
        public override void Close() => script.Close();
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => script.CreateCommand();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new Transaction(this, isolationLevel, commit);

        private sealed class Transaction(DbConnection connection, IsolationLevel isolationLevel, Action? commit) : DbTransaction
        {
            protected override DbConnection DbConnection => connection;
            public override IsolationLevel IsolationLevel => isolationLevel;
            public override void Commit() => (commit ?? throw new InvalidOperationException("A rejected write cannot be committed."))();
            public override void Rollback() { }
        }
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
