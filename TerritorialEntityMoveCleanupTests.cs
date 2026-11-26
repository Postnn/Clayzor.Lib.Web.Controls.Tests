using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clayzor.Lib.Entities.MedicalTests;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет перенос через реальный адаптер формы, SQL-фикстуру и браузерную границу.</summary>
public sealed class TerritorialEntityMoveCleanupTests
{
    [Fact]
    public async Task AutomaticCleanupRetriesWithoutRepeatingConfirmedMove()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var parent = tree.RootNodes.Single(node => node.Id == "5");
        var destination = tree.RootNodes.Single(node => node.Id == "9");
        var child = ChildOf(parent);
        await form.InvokeAsync(() => tree.HandleNodeClick(parent));
        var url = form.Find("iframe").GetAttribute("src")!;
        database.QueuePane("Старый родитель");

        var module = context.JSInterop.SetupModule("./js/territorialEntitySearch.js");
        var failCleanup = true;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!failCleanup) return false;
            failCleanup = false;
            return true;
        })
            .SetException(new JSException("Очистка недоступна"));
        module.Setup<bool>("refreshMetadataFrame", _ => true).SetResult(true);

        var moved = await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination));

        Assert.True(moved);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.DoesNotContain("Перенос не подтверждён", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Equal("Территории в Старый родитель", form.Find("iframe").GetAttribute("title"));
        Assert.Equal(url, form.Find("iframe").GetAttribute("src"));
        Assert.Equal(9, database.ParentId);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.commitOperation"].Count);
        Assert.All(context.JSInterop.Invocations["clayDraftStore.commitOperation"], call => Assert.Equal(11L, call.Arguments[1]));
        Assert.Empty(database.Reads);
    }

    [Fact]
    public async Task LostCommitAcknowledgementKeepsMoveBlockedAndHidesCleanupRetry()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection { FailCommitAcknowledgement = true };
        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Contains("Результат переноса не подтверждён", form.Markup);
        Assert.Contains("Сверить родителя", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        database.Reads.Enqueue(Script.Rows(Script.Row(("Id", 8), ("ParentId", 9), ("Name", "Перенесённая территория"))));
        await form.FindAll("button").Single(button => button.TextContent.Trim() == "Сверить родителя").ClickAsync(new());
        Assert.Contains("Сейчас: #9", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Equal(9, database.ParentId);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Empty(database.Reads);
    }

    [Fact]
    public async Task SuspendedPreparationLeavesTheDomainUntouched()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var form = RenderSearch(context, database, failBegin: true);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Contains("Запись не начата", form.Markup);
        Assert.DoesNotContain("Сверить родителя", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Equal(0, database.Updates);
        Assert.Equal(0, database.Commits);
        Assert.Equal(3, database.Reads.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ReopenedJournalOnlyFinishesAnExactlyMatchedKnownOutcome(bool matching, bool rejected)
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var id = Guid.NewGuid();
        const long revision = 11;
        var intent = JsonSerializer.Serialize(new
        {
            From = new { Id = 8, ParentId = 5, Left = 2, Right = 3 },
            To = new { Id = 9, ParentId = (int?)null, Left = 11, Right = 14 },
            AttemptId = id,
        });
        var storedOperation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = id.ToString("N"), kind = "territory.move", status = "confirmed", revision,
            data = intent, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        var result = JsonSerializer.Serialize(new
        {
            AttemptId = matching ? id.ToString("N") : "other-attempt",
            Revision = revision,
            Rejection = rejected ? "Конкурентный перенос уже изменил строку." : null,
        });
        var form = RenderSearch(context, database,
            new ClayDraftRestore("move-fixture", true, revision, null, storedOperation,
                new Dictionary<string, string> { ["move-result"] = result }));
        if (matching)
        {
            var commit = Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
            Assert.Equal(revision, commit.Arguments[1]);
            if (rejected) Assert.Contains("Конкурентный перенос уже изменил строку.", form.Markup);
        }
        else
        {
            Assert.Contains("Результат переноса не подтверждён", form.Markup);
            Assert.DoesNotContain("Повторить очистку", form.Markup);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        }
        Assert.Equal(0, database.Updates);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    private sealed class MoveAcknowledgement(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var value = await inner.InvokeAsync<T>(identifier, cancellationToken, args);
            if (identifier != "clayDraftStore.beginOperationIntent" || value is not string json) return value;
            using var requested = JsonDocument.Parse((string)args![1]!);
            var result = JsonNode.Parse(json)!;
            foreach (var name in new[] { "attemptId", "kind", "data" })
                result[name] = requested.RootElement.GetProperty(name).GetString();
            return (T)(object)result.ToJsonString();
        }
    }

    [Fact]
    public async Task LegacyMarkerDoesNotPermitANewMoveOrAnOutcomeGuess()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        context.JSInterop.Setup<string?>("sessionStorage.getItem", invocation => Equals(invocation.Arguments[0], "territory-move:4"))
            .SetResult("""{"From":{"Id":8,"ParentId":5,"Left":2,"Right":3},"To":{"Id":9,"ParentId":null,"Left":11,"Right":14}}""");
        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Contains("Результат переноса не подтверждён", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, database.Updates);
    }

    private static ClayTreeNode ChildOf(ClayTreeNode parent) => new()
    {
        Id = "8", RawId = 8, Left = 2, Right = 3, Parent = parent,
    };

    private static IRenderedComponent<TerritorialEntitySearch> RenderSearch(BunitContext context, MoveConnection database,
        ClayDraftRestore? restore = null, bool failBegin = false)
    {
        DapperColumnMapper.Initialize();
        database.QueueAuth();
        database.QueueRoots();
        if (restore is null)
        {
            database.Reads.Enqueue(Script.Rows(Script.Row(("Id", 4))));
            database.QueueAuth();
        }
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(restore ?? new ClayDraftRestore("move-fixture", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true)
            .SetResult([]);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        var begin = context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true);
        if (failBegin)
            begin.SetException(new JSException("Хранилище недоступно"));
        else
            begin.SetResult("""{"version":1,"attemptId":"fixture","kind":"territory.move","status":"running","revision":11,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddScoped(_ => new ClayDraftService(new MoveAcknowledgement(context.JSInterop.JSRuntime)));
        var errorHandler = new FakeSqlErrorHandler();
        context.Services.AddSingleton<ISqlErrorHandler>(errorHandler);
        context.Services.AddScoped(_ => new DbManager("Server=fixture", errorHandler, () => database));
        context.Services.AddSingleton<AuthenticationStateProvider, MoveAuthentication>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddMudServices();
        context.Services.AddClayTree(new ConfigurationBuilder().Build());
        context.ComponentFactories.AddStub<InventoryMainMenu>();
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/TerritorialEntity/TerritorialEntity_search?fixture=1");
        var form = context.Render<TerritorialEntitySearch>();
        form.WaitForAssertion(() => Assert.Equal(2, form.FindComponent<ClayTreeView>().Instance.RootNodes.Count));
        return form;
    }

    private sealed class MoveAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class MoveConnection : DbConnection
    {
        private readonly ScriptedConnection _reads;
        private MoveTransaction? _transaction;

        internal MoveConnection() => _reads = new ScriptedConnection(Reads, emulateEquipmentIdentity: false);
        internal Queue<ScriptEntry> Reads { get; } = [];
        internal int ParentId { get; private set; } = 5;
        internal int Updates { get; private set; }
        internal int Commits { get; private set; }
        internal bool FailCommitAcknowledgement { get; init; }

        internal void QueueAuth()
        {
            Reads.Enqueue(Script.Rows(Script.Row(("Id", 4))));
            Reads.Enqueue(Script.Rows());
            Reads.Enqueue(Script.Rows(Script.Row(("TerritoryUpdate", true))));
        }

        internal void QueueRoots() => Reads.Enqueue(Script.Rows(
            Script.Row(("Id", 5), ("Text", "Старый родитель"), ("Type", 3), ("Left", 1), ("Right", 10)),
            Script.Row(("Id", 9), ("Text", "Новый родитель"), ("Type", 3), ("Left", 11), ("Right", 14))));

        internal void QueuePane(string name)
        {
            Reads.Enqueue(Script.Rows(Script.Row(("Id", 5))));
            Reads.Enqueue(Script.Rows(Script.Row(("Id", 5), ("Name", name), ("Type", 3))));
        }

        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => _reads.State;
        public override void Open() => _reads.Open();
        public override void Close() => _reads.Close();
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => _transaction is null ? _reads.CreateCommand() : new MoveCommand(this);
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => _transaction = new MoveTransaction(this);

        private sealed class MoveTransaction(MoveConnection owner) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection => owner;
            public override void Commit()
            {
                owner.Commits++;
                if (owner.FailCommitAcknowledgement) throw new IOException("Ответ COMMIT потерян");
            }
            public override void Rollback() => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                owner._transaction = null;
                base.Dispose(disposing);
            }
        }

        private sealed class MoveCommand(MoveConnection owner) : DbCommand
        {
            private readonly FakeParameterCollection _parameters = new();
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection => _parameters;
            protected override DbParameter CreateDbParameter() => new FakeParameter();
            public override void Prepare() { }
            public override void Cancel() { }
            public override object? ExecuteScalar() => throw new NotSupportedException();

            public override int ExecuteNonQuery()
            {
                Assert.StartsWith("UPDATE dbo.Территории SET Parent=", CommandText);
                Assert.Equal(8, _parameters["Id"].Value);
                owner.ParentId = Assert.IsType<int>(_parameters["ParentId"].Value);
                owner.Updates++;
                return 1;
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                Assert.Contains("WITH (UPDLOCK,HOLDLOCK)", CommandText);
                var rows = new DataTable();
                foreach (var name in new[] { "Id", "ParentId", "Left", "Right" }) rows.Columns.Add(name, typeof(int));
                switch (Assert.IsType<int>(_parameters["Id"].Value))
                {
                    case 8: rows.Rows.Add(8, owner.ParentId, 2, 3); break;
                    case 9: rows.Rows.Add(9, DBNull.Value, 11, 14); break;
                    default: throw new InvalidOperationException("Неожиданный ID");
                }
                return rows.CreateDataReader();
            }
        }
    }
}
