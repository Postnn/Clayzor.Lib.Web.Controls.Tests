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

/// <summary>
/// Тесты прерывания success-path при mutation SqlException (CTFR3.1 / CTFR3.2).
/// Fake IClayTreeMutations — через DI (TableName=null), не через _mutationsCached.
/// </summary>
public class ClayTreeMutationAbortTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public ClayTreeMutationAbortTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IClayTreeStateStore>(new FakeStateStore());
        _ctx.Services.AddSingleton<ISqlErrorHandler>(new FakeErrorHandler());
        _ctx.Services.AddSingleton<DbManager>(_ => new DbManager("Server=.", new FakeErrorHandler()));
    }

    public void Dispose() { try { _ctx.Dispose(); } catch { } }

    private sealed class FakeStateStore : IClayTreeStateStore
    {
        public int SaveCallCount { get; set; }
        public Task<ClayTreeState?> LoadAsync(string treeId, CancellationToken ct = default)
            => Task.FromResult<ClayTreeState?>(null);
        public Task SaveAsync(string treeId, ClayTreeState state, CancellationToken ct = default)
        { SaveCallCount++; return Task.CompletedTask; }
    }

    private sealed class FakeErrorHandler : ISqlErrorHandler
    {
        public void HandleSqlError(SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters) { }
    }

    /// <summary>Fake mutations — бросает SqlException.</summary>
    private sealed class ThrowingMutations : IClayTreeMutations
    {
        public bool AddChildCalled { get; private set; }
        public bool DeleteCalled { get; private set; }
        public bool ReorderCalled { get; private set; }
        public bool ReparentCalled { get; private set; }

        public Task AddChildAsync(object? parentId, string editColumn, string value, CancellationToken ct = default)
        { AddChildCalled = true; throw SqlExceptionFactory.Create(); }

        public Task UpdateNodeAsync(object nodeId, string editColumn, string value, CancellationToken ct = default)
            => throw SqlExceptionFactory.Create();

        public Task DeleteAsync(object nodeId, CancellationToken ct = default)
        { DeleteCalled = true; throw SqlExceptionFactory.Create(); }

        public Task ReorderAsync(object nodeId, object? parentId, long newLeftValue, CancellationToken ct = default)
        { ReorderCalled = true; throw SqlExceptionFactory.Create(); }

        public Task ReparentAsync(object nodeId, object? newParentId, CancellationToken ct = default)
        { ReparentCalled = true; throw SqlExceptionFactory.Create(); }

        public Task<string> GetNodePathAsync(object nodeId, string functionName,
            ClayTreePathDirection direction, CancellationToken ct = default)
            => Task.FromResult("/test");

        public Task<bool> IsDescendantAsync(object candidateDescendantId, object ancestorId,
            CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class FakeDs : IClayTreeDataSource
    {
        public int LoadCallCount { get; set; }
        public Task<ClayTreeLoadResult> LoadLevelAsync(ClayTreeLoadRequest request, CancellationToken ct = default)
        {
            LoadCallCount++;
            if (request.Parent is null)
                return Task.FromResult(new ClayTreeLoadResult([
                    new ClayTreeNode { Id = "A", Text = "A", HasChildren = true }
                ]));
            return Task.FromResult(new ClayTreeLoadResult([
                new ClayTreeNode { Id = "B", Text = "B", HasChildren = false }
            ]));
        }
    }

    private (ClayTreeView View, IRenderedComponent<ClayTreeView> Cut) CreateView(
        ThrowingMutations mutations)
    {
        var options = new ClayTreeOptions
        {
            TreeId = "test", SelectSql = "SELECT 1",
            TableName = null, // DI path — CTFR3.2
            EnableAddChild = true, EnableDelete = true, EnableEdit = true,
            EnableDragDrop = true,
            EditColumn = "Name",
            ShowBusyOverlay = false, PersistExpandedState = true,
            Schema = new ClayTreeSchema { IdColumn = "Id", TextColumn = "Text" },
        };
        _ctx.Services.AddSingleton<IClayTreeMutations>(mutations);
        var cut = _ctx.Render<ClayTreeView>(p => p.Add(c => c.Options, options));
        return (cut.Instance, cut);
    }

    // ═══ AddChild ═══

    [Fact]
    public async Task AddChild_SqlException_DoesNotRunSuccessPath()
    {
        var mutations = new ThrowingMutations();
        var ds = new FakeDs();
        var (view, cut) = CreateView(mutations);
        SetField(view, "_dataSource", ds);

        await cut.InvokeAsync(view.LoadRootsAsync);
        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.HasChildren = false;
        aNode.IsExpanded = true;

        ds.LoadCallCount = 0;

        var method = typeof(ClayTreeView).GetMethod("InvokeAddChildAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try { await cut.InvokeAsync(() => (Task)method.Invoke(view, [aNode])!); }
        catch (TargetInvocationException) { }

        Assert.True(mutations.AddChildCalled);
        Assert.False(aNode.HasChildren);
        Assert.Equal(0, ds.LoadCallCount);
    }

    // ═══ Delete ═══

    [Fact]
    public async Task Delete_SqlException_DoesNotRunSuccessPath()
    {
        var mutations = new ThrowingMutations();
        var ds = new FakeDs();
        var (view, cut) = CreateView(mutations);
        SetField(view, "_dataSource", ds);

        await cut.InvokeAsync(view.LoadRootsAsync);
        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.HasChildren = true;
        aNode.IsExpanded = true;
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));

        ds.LoadCallCount = 0;

        var method = typeof(ClayTreeView).GetMethod("InvokeDeleteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try { await cut.InvokeAsync(() => (Task)method.Invoke(view, [aNode])!); }
        catch (TargetInvocationException) { }

        Assert.True(mutations.DeleteCalled);
        Assert.Equal(0, ds.LoadCallCount);
    }

    // ═══ DnD (Reorder) — CTFR3.2 ═══

    [Fact]
    public async Task DragDrop_SqlException_DoesNotRunSuccessPath()
    {
        var mutations = new ThrowingMutations();
        var ds = new FakeDs();
        var (view, cut) = CreateView(mutations);
        SetField(view, "_dataSource", ds);

        await cut.InvokeAsync(view.LoadRootsAsync);
        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.HasChildren = true;
        aNode.IsExpanded = true;
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));

        ds.LoadCallCount = 0;

        // Вызвать DoReorderAsync через reflection
        var reorderMethod = typeof(ClayTreeView).GetMethod("DoReorderAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            await cut.InvokeAsync(() => (Task)reorderMethod.Invoke(view,
                [aNode, aNode.Children[0], "after"])!);
        }
        catch (TargetInvocationException) { }

        Assert.True(mutations.ReorderCalled);
        Assert.Equal(0, ds.LoadCallCount);
    }

    // ═══ SaveState best-effort ═══

    [Fact]
    public async Task SaveState_SqlException_SwallowedAsBestEffort()
    {
        var mutations = new ThrowingMutations();
        var ds = new FakeDs();
        var store = new ThrowingStateStore();
        var (view, cut) = CreateView(mutations);
        SetField(view, "_dataSource", ds);
        typeof(ClayTreeView).GetProperty("StateStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, store);

        await cut.InvokeAsync(view.LoadRootsAsync);

        var saveState = typeof(ClayTreeView).GetMethod("SaveStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)saveState.Invoke(view, [])!);
        // Не упало = best-effort работает
        Assert.True(store.SaveCalled);
    }

    private sealed class ThrowingStateStore : IClayTreeStateStore
    {
        public bool SaveCalled { get; private set; }
        public Task<ClayTreeState?> LoadAsync(string treeId, CancellationToken ct = default)
            => Task.FromResult<ClayTreeState?>(null);
        public Task SaveAsync(string treeId, ClayTreeState state, CancellationToken ct = default)
        { SaveCalled = true; throw SqlExceptionFactory.Create(); }
    }

    private static void SetField(object target, string name, object value)
    {
        typeof(ClayTreeView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }
}
