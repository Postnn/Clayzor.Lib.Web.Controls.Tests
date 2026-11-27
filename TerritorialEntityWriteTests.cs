using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class TerritorialEntityWriteTests
{
    private static readonly DateTime ChangedAt = new(2026, 11, 26);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateNavigatesToVerifiedReturnedKeyAfterKnownCleanup(bool failCleanup)
    {
        await using var fixture = new Fixture("id=0&parent=42",
        [
            Employee(), Script.Rows(), Access(insert: true),
            Parent(), Script.Rows(),
            Employee(), Access(insert: true), Employee(), Access(insert: true),
            Script.Scalar(42),
            Script.Scalar(43), Record(43, 42),
        ]);
        fixture.Context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ => failCleanup)
            .SetException(new JSException("Тестовый отказ завершения."));
        var page = fixture.Render();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("input[data-draft-key='territory.name']")));
        await page.InvokeAsync(() => page.FindComponent<ClayDropDownList<TerritorialEntityOption, int?>>()
            .Instance.ValueChanged.InvokeAsync((int)ТипыТерриитории.Страны));
        await page.InvokeAsync(() => page.Find("input[data-draft-key='territory.name']").InputAsync("Страна"));
        await page.InvokeAsync(() => page.Find("input[data-draft-key='territory.country-code']").InputAsync("1"));
        await page.InvokeAsync(() => page.Find("input[data-draft-key='territory.direction']").InputAsync("Север"));
        page.WaitForAssertion(() => Assert.False(Button(page, "Применить").HasAttribute("disabled")));
        await page.InvokeAsync(() => Button(page, "Применить").ClickAsync());

        Assert.True(fixture.Commits == 1, page.Markup + string.Join(Environment.NewLine, fixture.Connection.CommandLog));
        Assert.Single(fixture.Connection.CommandLog, sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal));
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        var outcome = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "card-result"));
        using var receipt = JsonDocument.Parse(Assert.IsType<string>(outcome.Arguments[2]));
        Assert.Equal(43, receipt.RootElement.GetProperty("ResultId").GetInt32());
        Assert.Equal(10, receipt.RootElement.GetProperty("Revision").GetInt64());

        if (failCleanup)
        {
            Assert.Contains("id=0", fixture.Navigation.Uri);
            failCleanup = false;
            await page.InvokeAsync(() => Button(page, "Обновить").ClickAsync());
        }
        Assert.Contains("id=43", fixture.Navigation.Uri);
        Assert.Single(fixture.Connection.CommandLog, sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal));
        Assert.Single(fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.refreshOpener"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReopenedCreateAcceptsOnlyTheReceiptTiedToTheConfirmedAttempt(bool matching)
    {
        var attempt = Guid.NewGuid();
        var origin = new TerritorialEntityForm.FormState(new TerritorialEntityRecord { ParentId = 42 }, null);
        var submitted = origin with
        {
            Record = origin.Record with { Name = "Страна", Type = ТипыТерриитории.Страны, CountryCode = "1", Direction = "Север" },
        };
        var data = JsonSerializer.Serialize(new { Original = origin, Current = submitted });
        var pending = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = attempt.ToString("N"), kind = "territory.create", status = "confirmed", revision = 10, data,
            scopes = new[] { new { name = "main", typedRevision = 9, rawRevisions = new Dictionary<string, int>() } },
            acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        var receipt = JsonSerializer.Serialize(new { AttemptId = attempt.ToString("N"), Revision = 10L, ResultId = 43 });
        await using var fixture = new Fixture("id=0&parent=42",
            [Employee(), Script.Rows(), Access(insert: true), Parent(), Script.Rows(),
                Employee(), Access(insert: true),
                Employee(), Script.Rows(), Employee(), Access(insert: true)],
            new ClayDraftRestore("territory-write", true, 10, null, pending,
                new Dictionary<string, string> { ["card-result"] = receipt, ["main"] = data }));
        var js = fixture.Context.JSInterop;
        js.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer",
            call => Equals(call.Arguments[0], "territory-form") && Equals(call.Arguments[1], "new") &&
                Equals(call.Arguments[2], "4"))
            .SetResult([new(attempt.ToString("N"), "territory.create", """{"Id":0,"ParentId":42}""")]);
        if (matching)
            await InventoryOperationJournal.RunSqlAsync<InvalidOperationException>(fixture.Database,
                InventoryOperationJournal.ForEmployee(new Employee { Id = 4 }, attempt, "territory/tree",
                    "territory.create", ["territory/card/new/42"]), (_, _, _) => Task.FromResult<int?>(43), default);
        var page = fixture.Render();
        page.WaitForAssertion(() => Assert.Contains("Обнаружена незавершённая операция", page.Markup));
        Assert.Single(js.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]);
        Assert.Single(js.Invocations["clayDraftStore.registerRam"]);
        Assert.Equal(!matching, fixture.Journal.HasLegacyEvidence(attempt));

        await page.InvokeAsync(() => Button(page, "Обновить").ClickAsync());

        if (matching)
        {
            Assert.Contains("id=43", fixture.Navigation.Uri, StringComparison.Ordinal);
            var close = Assert.Single(js.Invocations["clayDraftStore.closeRam"]);
            Assert.Equal("territory-write", close.Arguments[0]);
            Assert.Equal(10L, close.Arguments[1]);
        }
        else
        {
            Assert.Contains("id=0", fixture.Navigation.Uri);
            Assert.Empty(js.Invocations["clayDraftStore.closeRam"]);
        }
        Assert.Equal(matching ? 1 : 0, js.Invocations["clayDraftStore.commitOperation"].Count);
        Assert.Equal(0, fixture.Commits);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal));
    }


    [Fact]
    public async Task DeleteChecksOriginalThenCallsLegacyProcedureOnce()
    {
        await using var fixture = new Fixture("id=43",
        [
            Employee(), Script.Rows(), Access(delete: true), Record(43, 42), Script.Rows(),
            Employee(), Access(delete: true), Employee(), Access(delete: true), Record(43, 42), Script.NonQuery(), Script.Scalar(1), Script.Rows(),
        ]);
        var page = fixture.Render();
        page.WaitForAssertion(() => Assert.False(Button(page, "Удалить").HasAttribute("disabled")));
        var removing = page.InvokeAsync(() => Button(page, "Удалить").ClickAsync());
        fixture.Dialogs.WaitForAssertion(() => Assert.Contains("Удалить территорию", fixture.Dialogs.Markup));
        await fixture.Dialogs.InvokeAsync(() => Button(fixture.Dialogs, "Да").ClickAsync());
        await removing.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.True(fixture.Commits == 1, page.Markup + string.Join(Environment.NewLine, fixture.Connection.CommandLog));
        Assert.Single(fixture.Connection.CommandLog, sql => sql == "master.dbo.sp_Delete_Территория");
        var args = Assert.Single(fixture.Connection.ParameterLog.Where(item => item.ContainsKey("КодТерриторииУдаляемой")));
        Assert.Equal(43, args["КодТерриторииУдаляемой"]);
        Assert.Equal(0, args["КодТерриторииЗамещающей"]);
        Assert.Equal(1, args["Выполнить"]);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }

    [Theory]
    [InlineData("denied", false)]
    [InlineData("missing-parent", false)]
    [InlineData("missing-id", true)]
    public async Task CreateFailsClosedWhenPermissionParentOrIssuedKeyCannotBeProven(string scenario, bool insert)
    {
        await using var fixture = new Fixture("", scenario switch
        {
            "denied" => [Employee(), Access()],
            "missing-parent" => [Employee(), Access(insert: true), Employee(), Access(insert: true), Script.Rows()],
            _ => [Employee(), Access(insert: true), Employee(), Access(insert: true), Script.Scalar(0)],
        });
        var draft = new TerritorialEntityRecord
        {
            ParentId = scenario == "missing-parent" ? 42 : null, Type = ТипыТерриитории.Страны, Name = "Страна", CountryCode = "1", Direction = "Север",
        };
        if (scenario == "denied")
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => TerritorialEntityData.CreateTerritoryAsync(fixture.Database, "test", 4, Guid.NewGuid(), draft));
        else
            await Assert.ThrowsAsync<TerritorialEntityRuleException>(() => TerritorialEntityData.CreateTerritoryAsync(fixture.Database, "test", 4, Guid.NewGuid(), draft));

        Assert.Equal(insert, fixture.Connection.CommandLog.Any(sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal)));
        Assert.Equal(0, fixture.Commits);
    }

    [Theory]
    [InlineData("denied", false)]
    [InlineData("stale", false)]
    [InlineData("not-deleted", true)]
    public async Task DeleteDoesNotCommitWithoutPermissionMatchingBaselineAndObservedRemoval(string scenario, bool dispatch)
    {
        await using var fixture = new Fixture("", scenario switch
        {
            "denied" => [Employee(), Access()],
            "stale" => [Employee(), Access(delete: true), Employee(), Access(delete: true), Record(43, 41)],
            _ => [Employee(), Access(delete: true), Employee(), Access(delete: true), Record(43, 42), Script.NonQuery(), Script.Scalar(1), Record(43, 42)],
        });
        var original = new TerritorialEntityRecord
        {
            Id = 43, ParentId = 42, Type = ТипыТерриитории.Страны, Name = "Страна", CountryCode = "1",
            Direction = "Север", ChangedAt = ChangedAt,
        };
        if (scenario == "denied")
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => TerritorialEntityData.DeleteTerritoryAsync(fixture.Database, "test", 4, Guid.NewGuid(), original));
        else
            await Assert.ThrowsAsync<TerritorialEntityRuleException>(() => TerritorialEntityData.DeleteTerritoryAsync(fixture.Database, "test", 4, Guid.NewGuid(), original));

        Assert.Equal(dispatch, fixture.Connection.CommandLog.Any(sql => sql == "master.dbo.sp_Delete_Территория"));
        Assert.Equal(0, fixture.Commits);
    }

    /// <summary>Серверный ключ выдаётся из той же попытки после потери COMMIT и не требует нового INSERT.</summary>
    [Fact]
    public async Task CreateUsesExactReceiptWithoutRepeatingInsertAfterLostCommit()
    {
        await using var fixture = new Fixture("",
        [
            Employee(), Access(insert: true), Employee(), Access(insert: true), Script.Scalar(42), Script.Scalar(43), Record(43, 42),
            Employee(), Access(insert: true), Record(43, 42),
        ]);
        fixture.Journal.LoseNextConfirmedCommitReply = true;
        var id = Guid.NewGuid();
        var draft = new TerritorialEntityRecord
        {
            ParentId = 42, Type = ТипыТерриитории.Страны, Name = "Страна", CountryCode = "1", Direction = "Север",
        };

        var first = await TerritorialEntityData.CreateTerritoryAsync(fixture.Database, "test", 4, id, draft);
        var replay = await TerritorialEntityData.CreateTerritoryAsync(fixture.Database, "test", 4, id, draft);

        Assert.Equal(43, first.Id);
        Assert.Equal(first, replay);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(id));
        Assert.Single(fixture.Connection.CommandLog, sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal));
    }

    /// <summary>Одновременная операция дерева блокирует запись карточки до браузерного Begin.</summary>
    [Fact]
    public async Task CardOpensWithTheSharedTreeSlotBlocked()
    {
        await using var fixture = new Fixture("id=0&parent=42",
            [Employee(), Script.Rows(), Access(insert: true), Parent(), Script.Rows()]);
        var move = InventoryOperationJournal.ForEmployee(new Employee { Id = 8 }, Guid.NewGuid(), "territory/tree", "territory.move");
        await Assert.ThrowsAsync<TimeoutException>(() => InventoryOperationJournal.RunSqlAsync<InvalidOperationException>(
            fixture.Database, move, (_, _, _) => Task.FromException<int?>(new TimeoutException()), default));
        var page = fixture.Render();
        page.WaitForAssertion(() => Assert.Contains("Есть незавершённая серверная операция с деревом", page.Markup));
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Неизвестное состояние транзакции процедуры не становится подтверждённым удалением.</summary>
    [Fact]
    public async Task DeleteKeepsTheSlotWhenTheProcedureDoesNotPreserveTransaction()
    {
        await using var fixture = new Fixture("",
        [
            Employee(), Access(delete: true), Employee(), Access(delete: true), Record(43, 42), Script.NonQuery(), Script.Scalar(0),
        ]);
        var original = new TerritorialEntityRecord
        {
            Id = 43, ParentId = 42, Type = ТипыТерриитории.Страны, Name = "Страна", CountryCode = "1",
            Direction = "Север", ChangedAt = ChangedAt,
        };
        var attempt = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TerritorialEntityData.DeleteTerritoryAsync(fixture.Database, "test", 4, attempt, original));

        Assert.Equal(InventoryOperationState.Reserved, fixture.Journal.StateFor(attempt));
        Assert.Single(fixture.Connection.CommandLog, sql => sql == "master.dbo.sp_Delete_Территория");
    }


    /// <summary>Другая родительская ветвь старого редактора создания тоже блокирует дерево после импорта.</summary>
    [Fact]
    public async Task CreationTransfersOtherParentAliasBeforeOpeningCurrentRamScope()
    {
        var attempt = Guid.NewGuid();
        await using var fixture = new Fixture("id=0&parent=42",
            [Employee(), Script.Rows(), Access(insert: true), Parent(), Script.Rows(), Employee(), Access(insert: true)]);
        var js = fixture.Context.JSInterop;
        js.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer",
            call => Equals(call.Arguments[0], "territory-form") && Equals(call.Arguments[1], "new") &&
                Equals(call.Arguments[2], "4"))
            .SetResult([new(attempt.ToString("N"), "territory.create", """{"Id":0,"ParentId":77}""")]);
        js.Setup<ClayDraftRestore?>("clayDraftStore.registerRam",
            call => fixture.Journal.HasLegacyEvidence(attempt) && Equals(call.Arguments[1], "new:42") &&
                js.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"].Count == 1)
            .SetResult(new ClayDraftRestore("territory-write", false, 0, null, null));
        var page = fixture.Render();

        page.WaitForAssertion(() => Assert.Contains("Есть незавершённая серверная операция с деревом", page.Markup));
        Assert.Contains("territory/card/new/77", fixture.Journal.ScopesFor(attempt));
        Assert.Null(fixture.Journal.StateFor(attempt));
        Assert.Single(js.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(js.Invocations["clayDraftStore.listRecoveryCandidates"]);
        Assert.Empty(js.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Чужой ID не открывает RAM и не создаёт серверную запись прежней попытки.</summary>
    [Fact]
    public async Task ExistingCardRejectsLegacyEvidenceForAnotherTerritory()
    {
        var attempt = Guid.NewGuid();
        await using var fixture = new Fixture("id=43",
            [Employee(), Script.Rows(), Access(delete: true), Record(43, 42), Script.Rows()]);
        var js = fixture.Context.JSInterop;
        js.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([new(attempt.ToString("N"), "territory.delete", """{"Id":44,"ParentId":42}""")]);
        var page = fixture.Render();

        page.WaitForAssertion(() => Assert.Single(js.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]));
        Assert.False(fixture.Journal.HasLegacyEvidence(attempt));
        Assert.Empty(js.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(js.Invocations["clayDraftStore.beginOperationIntent"]);
    }


    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> component, string title) where T : IComponent =>
        component.FindAll("button").Single(button => button.TextContent.Trim() == title);

    private static ScriptEntry Employee() => Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Test")));

    private static ScriptEntry Access(bool insert = false, bool delete = false) =>
        Script.Rows(Script.Row(("TerritoryInsert", insert), ("TerritoryDelete", delete)));

    private static ScriptEntry Parent() => Script.Rows(Script.Row(("Id", 42), ("Type", 1), ("Name", "Родитель")));

    private static ScriptEntry Record(int id, int? parent) => Script.Rows(Script.Row(
        ("Id", id), ("ParentId", parent), ("Type", 2), ("Name", "Страна"), ("CountryCode", "1"),
        ("Direction", "Север"), ("ChangedAt", ChangedAt)));

    private sealed class Fixture : IAsyncDisposable
    {
        internal Fixture(string route, IEnumerable<ScriptEntry> commands, ClayDraftRestore? restore = null)
        {
            DapperColumnMapper.Initialize();
            Connection = new ScriptedConnection(new Queue<ScriptEntry>(commands), emulateEquipmentIdentity: false);
            Journal = new NetFormTests.NetJournalConnection(Connection);
            Database = new DbManager("Server=test", new FakeSqlErrorHandler(), () => Journal);
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            Context.Services.AddSingleton(Database);
            Context.Services.AddScoped(_ => new ClayDraftService(new BeginAcknowledgement(Context.JSInterop.JSRuntime)));
            Context.Services.AddScoped<ClayFormWindowService>();
            Context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            Context.Services.AddSingleton<AuthenticationStateProvider, TrustedAuthentication>();
            Context.Services.AddMudServices();
            Context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>(
                "clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true).SetResult([]);
            Context.JSInterop.SetupVoid("clayDraftStore.finishLegacyOperationEvidenceTransfer", _ => true).SetVoidResult();
            Context.JSInterop.SetupVoid("clayDraftStore.retireLegacyOperationEvidenceTransfer", _ => true).SetVoidResult();
            Context.JSInterop.Setup<bool>("clayDraftStore.closeRam", call => call.Arguments.Count == 2).SetResult(true);
            Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
                .SetResult(restore ?? new ClayDraftRestore("territory-write", false, 7, null, null));
            Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], []));
            Context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
                .SetResult(new ClayDraftRestoreReadiness(true, [], []));
            Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
                .SetResult(new ClayDraftEditorScopeState(10, false, false));
            Context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
                .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 7, 7));
            Context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
                .SetResult("""{"version":1,"attemptId":"placeholder","kind":"placeholder","status":"running","revision":10,"data":"{}","scopes":[{"name":"main","typedRevision":9,"rawRevisions":{}}],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
            Dialogs = Context.Render<MudDialogProvider>();
            Context.Render<MudPopoverProvider>();
            Navigation = Context.Services.GetRequiredService<NavigationManager>();
            Navigation.NavigateTo($"/TerritorialEntity/TerritorialEntity_form?{route}");
        }

        internal BunitContext Context { get; } = new();
        internal ScriptedConnection Connection { get; }
        internal DbManager Database { get; }
        internal NavigationManager Navigation { get; }
        internal IRenderedComponent<MudDialogProvider> Dialogs { get; }
        internal NetFormTests.NetJournalConnection Journal { get; }

        internal int Commits => Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"].Count(call =>
        {
            using var payload = JsonDocument.Parse(Assert.IsType<string>(call.Arguments[1]));
            var id = Guid.Parse(payload.RootElement.GetProperty("attemptId").GetString()!);
            return Journal.StateFor(id) == InventoryOperationState.Confirmed;
        });

        internal IRenderedComponent<TerritorialEntityForm> Render() => Context.Render<TerritorialEntityForm>();

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class BeginAcknowledgement(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var value = await inner.InvokeAsync<T>(identifier, cancellationToken, args);
            if (identifier != "clayDraftStore.beginOperationIntent" || value is not string json) return value;
            using var input = JsonDocument.Parse((string)args![1]!);
            var result = JsonNode.Parse(json)!;
            foreach (var name in new[] { "attemptId", "kind", "data" })
                result[name] = input.RootElement.GetProperty(name).GetString();
            return (T)(object)result.ToJsonString();
        }
    }

    private sealed class TrustedAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
