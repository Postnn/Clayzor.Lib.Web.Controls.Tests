using System.Data;
using System.Data.Common;
using System.Security.Claims;
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
    public async Task BrowsingCleanupFailureKeepsConfirmedCommitAndRetryOnlyRefreshes()
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
        context.JSInterop.SetupVoid("sessionStorage.removeItem", _ => failCleanup)
            .SetException(new JSException("Очистка недоступна"));
        module.Setup<bool>("clearMoveMarker", _ => failCleanup).SetException(new JSException("Темпоральная ошибка"));
        module.Setup<bool>("clearMoveMarker", _ => !failCleanup).SetResult(true);
        module.Setup<bool>("refreshMetadataFrame", _ => true).SetResult(true);

        var moved = await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination));

        Assert.True(moved);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.Contains("Перенос подтверждён", form.Markup);
        Assert.DoesNotContain("Перенос не подтверждён", form.Markup);
        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));

        database.QueuePane("Родитель после переноса");
        database.QueueRoots();
        database.Reads.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        failCleanup = false;
        await form.FindAll("button").Single(button => button.TextContent.Trim() == "Повторить очистку").ClickAsync(new());

        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Equal("Территории в Родитель после переноса", form.Find("iframe").GetAttribute("title"));
        Assert.Equal(url, form.Find("iframe").GetAttribute("src"));
        Assert.Equal(9, database.ParentId);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        var saved = Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "sessionStorage.setItem" &&
            Equals(call.Arguments[0], "territory-move:4"));
        var removals = context.JSInterop.Invocations.Where(call => call.Identifier == "clearMoveMarker").ToArray();
        Assert.Equal(2, removals.Length);
        Assert.All(removals, removal => Assert.Equal(saved.Arguments, removal.Arguments));
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
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clearMoveMarker");
        Assert.Empty(database.Reads);
    }

    [Fact]
    public async Task SuspendedPreparationLeavesTheDomainUntouched()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");
        context.JSInterop.SetupVoid("sessionStorage.setItem", call => Equals(call.Arguments[0], "territory-move:4"))
            .SetException(new JSException("Хранилище недоступно"));

        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Contains("Запись не начата", form.Markup);
        Assert.DoesNotContain("Сверить родителя", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Equal(0, database.Updates);
        Assert.Equal(0, database.Commits);
        Assert.Equal(3, database.Reads.Count);
    }

    private static ClayTreeNode ChildOf(ClayTreeNode parent) => new()
    {
        Id = "8", RawId = 8, Left = 2, Right = 3, Parent = parent,
    };

    private static IRenderedComponent<TerritorialEntitySearch> RenderSearch(BunitContext context, MoveConnection database)
    {
        database.QueueAuth();
        database.QueueRoots();
        database.QueueAuth();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
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
