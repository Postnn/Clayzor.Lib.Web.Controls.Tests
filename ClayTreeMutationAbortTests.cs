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

/// <summary>
/// SqlException factory через reflection.
/// </summary>
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
/// Тесты прерывания success-path при mutation SqlException (CTFR3.1).
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

        public Task AddChildAsync(object? parentId, string editColumn, string value, CancellationToken ct = default)
        { AddChildCalled = true; throw SqlExceptionFactory.Create(); }

        public Task UpdateNodeAsync(object nodeId, string editColumn, string value, CancellationToken ct = default)
            => throw SqlExceptionFactory.Create();

        public Task DeleteAsync(object nodeId, CancellationToken ct = default)
        { DeleteCalled = true; throw SqlExceptionFactory.Create(); }

        public Task ReorderAsync(object nodeId, object? parentId, long newLeftValue, CancellationToken ct = default)
            => throw SqlExceptionFactory.Create();

        public Task ReparentAsync(object nodeId, object? newParentId, CancellationToken ct = default)
            => throw SqlExceptionFactory.Create();

        public Task<string> GetNodePathAsync(object nodeId, string functionName,
            ClayTreePathDirection direction, CancellationToken ct = default)
            => Task.FromResult("/test");

        public Task<bool> IsDescendantAsync(object candidateDescendantId, object ancestorId,
            CancellationToken ct = default)
            => Task.FromResult(false);
    }

    /// <summary>Fake data source с одним ребёнком.</summary>
    private sealed class FakeDs : IClayTreeDataSource
    {
        public int LoadCallCount { get; set; }
        public Task<ClayTreeLoadResult> LoadLevelAsync(ClayTreeLoadRequest request, CancellationToken ct = default)
        {
            LoadCallCount++;
            if (request.Parent is null)
            {
                return Task.FromResult(new ClayTreeLoadResult([
                    new ClayTreeNode { Id = "A", Text = "A", HasChildren = true }
                ]));
            }
            return Task.FromResult(new ClayTreeLoadResult([
                new ClayTreeNode { Id = "B", Text = "B", HasChildren = false }
            ]));
        }
    }

    private (ClayTreeView View, IRenderedComponent<ClayTreeView> Cut) CreateView()
    {
        var options = new ClayTreeOptions
        {
            TreeId = "test", SelectSql = "SELECT 1", TableName = "dbo.T",
            EnableAddChild = true, EnableDelete = true, EnableEdit = true,
            EditColumn = "Name",
            ShowBusyOverlay = false, PersistExpandedState = false,
            Schema = new ClayTreeSchema { IdColumn = "Id", TextColumn = "Text" },
        };
        var cut = _ctx.Render<ClayTreeView>(p => p
            .Add(c => c.Options, options));
        return (cut.Instance, cut);
    }

    // ═══ AddChild — SqlException прерывает success-path ═══

    [Fact]
    public async Task AddChild_SqlException_DoesNotRunSuccessPath()
    {
        var ds = new FakeDs();
        var mutations = new ThrowingMutations();
        var (view, cut) = CreateView();

        // Inject fake data source + mutations
        SetField(view, "_dataSource", ds);
        typeof(ClayTreeView).GetField("_mutationsCached", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, mutations);

        await cut.InvokeAsync(view.LoadRootsAsync);
        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        bool hadChildren = aNode.HasChildren;
        aNode.HasChildren = false; // pre-condition: no children
        aNode.IsExpanded = true;

        ds.LoadCallCount = 0;

        // Invoke real AddChildAsync (будет использовать ThrowingMutations)
        var addChildMethod = typeof(ClayTreeView).GetMethod("InvokeAddChildAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try { await cut.InvokeAsync(() => (Task)addChildMethod.Invoke(view, [aNode])!); }
        catch (TargetInvocationException) { /* SqlException from mutations */ }

        // Success-path НЕ выполнился:
        Assert.False(aNode.HasChildren, "HasChildren must remain false after failed AddChild");
        Assert.True(mutations.AddChildCalled, "AddChild must have been called");
        // Reload не вызван (datasource count not increased beyond initial load)
        Assert.Equal(1, ds.LoadCallCount); // only LoadRootsAsync
    }

    // ═══ Delete — SqlException не меняет selection ═══

    [Fact]
    public async Task Delete_SqlException_DoesNotRunSuccessPath()
    {
        var ds = new FakeDs();
        var mutations = new ThrowingMutations();
        var (view, cut) = CreateView();

        SetField(view, "_dataSource", ds);
        typeof(ClayTreeView).GetField("_mutationsCached", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, mutations);

        await cut.InvokeAsync(view.LoadRootsAsync);
        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.HasChildren = true;
        aNode.IsExpanded = true;
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));

        ds.LoadCallCount = 0;

        var deleteMethod = typeof(ClayTreeView).GetMethod("InvokeDeleteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try { await cut.InvokeAsync(() => (Task)deleteMethod.Invoke(view, [aNode])!); }
        catch (TargetInvocationException) { }

        Assert.True(mutations.DeleteCalled);
        // Reload НЕ вызван
        Assert.Equal(0, ds.LoadCallCount);
    }

    // ═══ SaveState — best-effort, SqlException не роняет UI ═══

    [Fact]
    public async Task SaveState_SqlException_SwallowedAsBestEffort()
    {
        var ds = new FakeDs();
        var stateStore = new FakeStateStore();
        var (view, cut) = CreateView();

        SetField(view, "_dataSource", ds);
        typeof(ClayTreeView).GetProperty("StateStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, new ThrowingStateStore());

        await cut.InvokeAsync(view.LoadRootsAsync);
        // Ловим ошибку через try — в production сохраняется через try/catch
        // Validate that SaveStateAsync doesn't throw
        var saveState = typeof(ClayTreeView).GetMethod("SaveStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // В production уже обёрнуто try/catch — тест проверяет, что не падает
        await cut.InvokeAsync(() => (Task)saveState.Invoke(view, [])!);
        // Не упало = best-effort работает
    }

    private sealed class ThrowingStateStore : IClayTreeStateStore
    {
        public Task<ClayTreeState?> LoadAsync(string treeId, CancellationToken ct = default)
            => Task.FromResult<ClayTreeState?>(null);
        public Task SaveAsync(string treeId, ClayTreeState state, CancellationToken ct = default)
            => throw SqlExceptionFactory.Create();
    }

    private static void SetField(object target, string name, object value)
    {
        typeof(ClayTreeView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }
}
