using System.Reflection;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.DataSources;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Components.Tree.State;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

internal static class SqlExceptionFactory
{
    public static SqlException Create(int number = 547, string message = "test")
    {
        var errCtor = typeof(SqlError).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)], null)!;
        var err = (SqlError)errCtor.Invoke([number, (byte)0, (byte)0, "srv", message, "", 0, null]);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, [err]);
        var exCtor = typeof(SqlException).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(string), typeof(SqlErrorCollection), typeof(Exception), typeof(Guid)], null)!;
        return (SqlException)exCtor.Invoke([message, errors, null, Guid.NewGuid()]);
    }
}

/// <summary>Проверяет прерывание мутаций дерева после SqlException с подставными данными и хранилищем.</summary>
public sealed class ClayTreeMutationAbortTests : IAsyncLifetime
{
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(5);
    private readonly BunitContext _ctx = new();
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;
    private int _sqlFactoryCalls;

    /// <summary>Запрещает открытие SQL и регистрирует сервисы настоящих тестовых диалогов.</summary>
    public ClayTreeMutationAbortTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.Services.AddMudExtensions();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<ISqlErrorHandler>(new FakeErrorHandler());
        _ctx.Services.AddSingleton<DbManager>(_ => new DbManager("Server=fixture", new FakeErrorHandler(),
            () =>
            {
                Interlocked.Increment(ref _sqlFactoryCalls);
                throw new InvalidOperationException("SQL connection requested by tree fixture.");
            }));
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        Assert.Equal(0, _sqlFactoryCalls);
    }

    private sealed class FakeStateStore : IClayTreeStateStore
    {
        public Task<ClayTreeState?> LoadAsync(string treeId, CancellationToken ct = default) =>
            Task.FromResult<ClayTreeState?>(null);

        public Task SaveAsync(string treeId, ClayTreeState state, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeErrorHandler : ISqlErrorHandler
    {
        public void HandleSqlError(SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters) { }
    }

    private sealed class ThrowingMutations : IClayTreeMutations
    {
        public bool AddChildCalled { get; private set; }
        public string? AddedValue { get; private set; }
        public bool DeleteCalled { get; private set; }
        public bool ReorderCalled { get; private set; }

        public Task AddChildAsync(object? parentId, string editColumn, string value, CancellationToken ct = default)
        {
            AddChildCalled = true;
            AddedValue = value;
            throw SqlExceptionFactory.Create();
        }

        public Task UpdateNodeAsync(object nodeId, string editColumn, string value, CancellationToken ct = default) =>
            throw SqlExceptionFactory.Create();

        public Task DeleteAsync(object nodeId, CancellationToken ct = default)
        {
            DeleteCalled = true;
            throw SqlExceptionFactory.Create();
        }

        public Task ReorderAsync(object nodeId, object? parentId, long newLeftValue, CancellationToken ct = default)
        {
            ReorderCalled = true;
            throw SqlExceptionFactory.Create();
        }

        public Task ReparentAsync(object nodeId, object? newParentId, CancellationToken ct = default) =>
            throw SqlExceptionFactory.Create();

        public Task<string> GetNodePathAsync(object nodeId, string functionName,
            ClayTreePathDirection direction, CancellationToken ct = default) =>
            Task.FromResult("/test");

        public Task<bool> IsDescendantAsync(object candidateDescendantId, object ancestorId,
            CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeDs : IClayTreeDataSource
    {
        public int LoadCallCount { get; set; }

        public Task<ClayTreeLoadResult> LoadLevelAsync(ClayTreeLoadRequest request, CancellationToken ct = default)
        {
            LoadCallCount++;
            var result = request.Parent is null
                ? new ClayTreeLoadResult([
                    new ClayTreeNode { Id = "A", RawId = "A", Text = "A", HasChildren = true, Left = 1, Right = 4 },
                    new ClayTreeNode { Id = "C", RawId = "C", Text = "C", Left = 5, Right = 6 },
                ])
                : new ClayTreeLoadResult([
                    new ClayTreeNode { Id = "B", RawId = "B", Text = "B", ParentId = "A", Left = 2, Right = 3 },
                ]);
            return Task.FromResult(result);
        }
    }

    private IRenderedComponent<ClayTreeView> CreateView(ThrowingMutations mutations, FakeDs dataSource,
        IClayTreeStateStore? stateStore = null)
    {
        var options = new ClayTreeOptions
        {
            TreeId = "test",
            SelectSql = "SELECT 1",
            TableName = null,
            EnableAddChild = true,
            EnableDelete = true,
            EnableEdit = true,
            EnableDragDrop = true,
            EditColumn = "Name",
            ShowBusyOverlay = false,
            PersistExpandedState = true,
            Schema = new ClayTreeSchema { IdColumn = "Id", TextColumn = "Text" },
        };
        _ctx.Services.AddSingleton<IClayTreeMutations>(mutations);
        _ctx.Services.AddSingleton<IClayTreeStateStore>(stateStore ?? new FakeStateStore());
        _dialogs = _ctx.Render<MudDialogProvider>();
        var cut = _ctx.Render<ClayTreeView>(p => p.Add(c => c.Options, options).Add(c => c.DataSource, dataSource));
        cut.WaitForAssertion(() => Assert.Equal(["A", "C"], cut.Instance.RootNodes.Select(node => node.Id)));
        return cut;
    }

    private async Task CompleteDialogAsync(Task operation, string message, string confirmation, string? input = null)
    {
        _dialogs.WaitForAssertion(() => Assert.Contains(message, _dialogs.Markup), DialogTimeout);
        if (input is not null)
            await _dialogs.InvokeAsync(() => _dialogs.Find("input").InputAsync(input));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button")
            .Single(button => button.TextContent.Trim() == confirmation).ClickAsync());
        await operation.WaitAsync(DialogTimeout);
    }

    [Fact]
    public async Task AddChild_SqlException_DoesNotRunSuccessPath()
    {
        var mutations = new ThrowingMutations();
        var ds = new FakeDs();
        var cut = CreateView(mutations, ds);
        var node = cut.Instance.RootNodes[0];
        node.HasChildren = false;
        node.IsExpanded = true;
        ds.LoadCallCount = 0;

        var pending = cut.InvokeAsync(() => cut.Instance.InvokeAddChildAsync(node));
        await CompleteDialogAsync(pending, "Добавление узла", "ОК", "Новый узел");

        Assert.True(mutations.AddChildCalled);
        Assert.Equal("Новый узел", mutations.AddedValue);
        Assert.False(node.HasChildren);
        Assert.Equal(0, ds.LoadCallCount);
    }

    [Fact]
    public async Task Delete_SqlException_DoesNotRunSuccessPath()
    {
        var mutations = new ThrowingMutations();
        var ds = new FakeDs();
        var cut = CreateView(mutations, ds);
        var node = cut.Instance.RootNodes[0];
        node.IsExpanded = true;
        await cut.InvokeAsync(() => cut.Instance.EnsureChildrenLoadedAsync(node));
        ds.LoadCallCount = 0;

        var pending = cut.InvokeAsync(() => cut.Instance.InvokeDeleteAsync(node));
        await CompleteDialogAsync(pending, "Вы уверены, что хотите удалить A?", "Да");

        Assert.True(mutations.DeleteCalled);
        Assert.Same(node, cut.Instance.RootNodes[0]);
        Assert.Single(node.Children);
        Assert.Equal(0, ds.LoadCallCount);
    }

    [Fact]
    public async Task DragDrop_SqlException_DoesNotRunSuccessPath()
    {
        var mutations = new ThrowingMutations();
        var ds = new FakeDs();
        var cut = CreateView(mutations, ds);
        var view = cut.Instance;
        var dragged = view.RootNodes[0];
        var target = view.RootNodes[1];
        ds.LoadCallCount = 0;

        var method = typeof(ClayTreeView).GetMethod("DoReorderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = cut.InvokeAsync(() => (Task)method.Invoke(view, [dragged, target, "after"])!);
        await CompleteDialogAsync(pending, "изменить порядок в рамках корневого уровня", "Да");

        Assert.True(mutations.ReorderCalled);
        Assert.Same(dragged, view.RootNodes[0]);
        Assert.Same(target, view.RootNodes[1]);
        Assert.Equal(0, ds.LoadCallCount);
    }

    [Fact]
    public async Task SaveState_SqlException_SwallowedAsBestEffort()
    {
        var store = new ThrowingStateStore();
        var cut = CreateView(new ThrowingMutations(), new FakeDs(), store);
        var method = typeof(ClayTreeView).GetMethod("SaveStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.True(store.SaveCalled);
    }

    private sealed class ThrowingStateStore : IClayTreeStateStore
    {
        public bool SaveCalled { get; private set; }

        public Task<ClayTreeState?> LoadAsync(string treeId, CancellationToken ct = default) =>
            Task.FromResult<ClayTreeState?>(null);

        public Task SaveAsync(string treeId, ClayTreeState state, CancellationToken ct = default)
        {
            SaveCalled = true;
            throw SqlExceptionFactory.Create();
        }
    }
}
