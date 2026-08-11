using System.Reflection;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.DataSources;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Components.Tree.State;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Behavioral tests paging restore через bUnit + реальный production ReloadLevelAsync (CTFR2.4).
/// </summary>
public class ClayTreePagingRestoreTests : IDisposable
{
    private readonly TestContext _ctx = new();
    private static readonly string RootKey = "__ROOT__";

    private sealed class FakePagingDataSource : IClayTreeDataSource
    {
        private readonly Dictionary<string, List<FakeNode>> _byParent = new();
        private readonly int _pageSize;

        public FakePagingDataSource(int pageSize) { _pageSize = pageSize; }
        public Dictionary<string, int> LoadCallCounts { get; } = new();

        public void AddChild(string? parentId, string id, bool hasChildren = false)
        {
            var key = parentId ?? RootKey;
            if (!_byParent.ContainsKey(key)) _byParent[key] = [];
            _byParent[key].Add(new FakeNode(id, hasChildren));
        }

        public void RemoveChild(string? parentId, string childId)
        {
            if (_byParent.TryGetValue(parentId ?? RootKey, out var list))
                list.RemoveAll(n => n.Id == childId);
        }

        public Task<ClayTreeLoadResult> LoadLevelAsync(ClayTreeLoadRequest request, CancellationToken ct = default)
        {
            var parentKey = request.Parent?.Id ?? RootKey;
            LoadCallCounts[parentKey] = LoadCallCounts.GetValueOrDefault(parentKey) + 1;

            if (!_byParent.TryGetValue(parentKey, out var allChildren) || allChildren.Count == 0)
                return Task.FromResult(new ClayTreeLoadResult([]));

            if (_pageSize <= 0)
            {
                var all = allChildren.Select((n, i) => new ClayTreeNode
                {
                    Id = n.Id, Text = n.Id, Left = i + 1, HasChildren = n.HasChildren,
                }).ToList();
                return Task.FromResult(new ClayTreeLoadResult(all));
            }

            var cursor = request.Parent?.LastChildCursor;
            var startIndex = cursor is long l ? (int)l : 0;

            var page = allChildren.Skip(startIndex).Take(_pageSize + 1)
                .Select((n, i) => new ClayTreeNode
                {
                    Id = n.Id, Text = n.Id,
                    Left = startIndex + i + 1, HasChildren = n.HasChildren,
                }).ToList();

            if (page.Count > _pageSize)
            {
                page.RemoveAt(page.Count - 1);
                return Task.FromResult(new ClayTreeLoadResult(page, null, true, page[^1].Left));
            }
            return Task.FromResult(new ClayTreeLoadResult(page));
        }

        private record FakeNode(string Id, bool HasChildren);
    }

    private sealed class FakeStateStore : IClayTreeStateStore
    {
        public Task<ClayTreeState?> LoadAsync(string treeId, CancellationToken ct = default)
            => Task.FromResult<ClayTreeState?>(null);
        public Task SaveAsync(string treeId, ClayTreeState state, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeErrorHandler : ISqlErrorHandler
    {
        public void HandleSqlError(Microsoft.Data.SqlClient.SqlException exception,
            string connectionString, string commandText,
            IReadOnlyList<(string Name, object? Value)> parameters) { }
    }

    public ClayTreePagingRestoreTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.Services.AddSingleton<IClayTreeStateStore>(new FakeStateStore());
        _ctx.Services.AddSingleton<ISqlErrorHandler>(new FakeErrorHandler());
        _ctx.Services.AddSingleton<DbManager>(_ => new DbManager("Server=.", new FakeErrorHandler()));
    }

    public void Dispose()
    {
        try { _ctx.Dispose(); } catch { }
    }

    private (ClayTreeView View, IRenderedComponent<ClayTreeView> Cut) CreateView(
        FakePagingDataSource ds, int levelPageSize)
    {
        var options = new ClayTreeOptions
        {
            TreeId = "test",
            SelectSql = "SELECT 1",
            LevelPageSize = levelPageSize,
            HierarchyMode = Clayzor.Lib.Entities.Tree.ClayTreeHierarchyMode.NestedSet,
            ShowBusyOverlay = false,
            PersistExpandedState = false,
            Schema = new Clayzor.Lib.Entities.Tree.ClayTreeSchema { IdColumn = "Id", TextColumn = "Text" },
        };

        // Render with bUnit
        var cut = _ctx.Render<ClayTreeView>(p => p
            .Add(c => c.Options, options)
            .Add(c => c.DataSource, ds));

        return (cut.Instance, cut);
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 1 — root reload, deep page 2 restore (REAL production)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReloadRoot_DeepExpandedChildOnPage2_RestoresViaProductionPaging()
    {
        var ds = new FakePagingDataSource(2);
        ds.AddChild(null, "A", hasChildren: true);
        ds.AddChild("A", "A1"); ds.AddChild("A", "A2");
        ds.AddChild("A", "A3", hasChildren: true);
        ds.AddChild("A", "A4");
        ds.AddChild("A3", "X");

        var (view, cut) = CreateView(ds, levelPageSize: 2);

        await cut.InvokeAsync(view.LoadRootsAsync);

        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.IsExpanded = true;
        SetExpanded(view, "A");

        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));
        await cut.InvokeAsync(() => view.LoadMoreChildrenAsync(aNode));
        Assert.Equal(4, aNode.Children.Count);

        var a3 = aNode.Children.First(c => c.Id == "A3");
        a3.IsExpanded = true;
        a3.HasChildren = true;
        SetExpanded(view, "A3");
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(a3));
        Assert.Single(a3.Children);

        ds.LoadCallCounts.Clear();

        // === REAL PRODUCTION ReloadLevelAsync(null) ===
        await cut.InvokeAsync(() => view.ReloadLevelAsync(null));

        var newRoots = ((IReadOnlyList<ClayTreeNode>)view.RootNodes);
        Assert.Single(newRoots);
        var newA = newRoots[0];
        Assert.True(newA.IsExpanded);
        Assert.Equal(4, newA.Children.Count);

        var newA3 = newA.Children.FirstOrDefault(c => c.Id == "A3");
        Assert.NotNull(newA3);
        Assert.True(newA3!.IsExpanded);
        Assert.NotSame(aNode, newA);

        Assert.True(ds.LoadCallCounts.GetValueOrDefault("A") >= 2,
            $"Expected >=2 loads, got {ds.LoadCallCounts.GetValueOrDefault("A")}");
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 2 — non-root reload, deep page 2 restore
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReloadNonRoot_DeepExpandedChildOnPage2_RestoresViaProductionPaging()
    {
        var ds = new FakePagingDataSource(2);
        ds.AddChild(null, "P", hasChildren: true);
        ds.AddChild("P", "A1"); ds.AddChild("P", "A2");
        ds.AddChild("P", "A3", hasChildren: true);
        ds.AddChild("P", "A4");

        var (view, cut) = CreateView(ds, levelPageSize: 2);
        await cut.InvokeAsync(view.LoadRootsAsync);

        var pNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        pNode.IsExpanded = true;
        SetExpanded(view, "P");
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(pNode));
        await cut.InvokeAsync(() => view.LoadMoreChildrenAsync(pNode));
        Assert.Equal(4, pNode.Children.Count);

        var a3 = pNode.Children.First(c => c.Id == "A3");
        a3.IsExpanded = true;
        SetExpanded(view, "A3");

        ds.LoadCallCounts.Clear();

        // === REAL PRODUCTION ReloadLevelAsync(parent) ===
        await cut.InvokeAsync(() => view.ReloadLevelAsync(pNode));

        Assert.Equal(4, pNode.Children.Count);
        var newA3 = pNode.Children.FirstOrDefault(c => c.Id == "A3");
        Assert.NotNull(newA3);
        Assert.True(newA3!.IsExpanded);

        Assert.True(ds.LoadCallCounts.GetValueOrDefault("P") >= 2,
            $"Expected >=2 loads, got {ds.LoadCallCounts.GetValueOrDefault("P")}");
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 3 — moved child stops at boundary
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reload_MovedExpandedChild_StopsAtPreviousBoundary()
    {
        var ds = new FakePagingDataSource(2);
        ds.AddChild(null, "A", hasChildren: true);
        for (var i = 1; i <= 20; i++)
            ds.AddChild("A", i == 3 ? "X" : $"C{i}");

        var (view, cut) = CreateView(ds, levelPageSize: 2);
        await cut.InvokeAsync(view.LoadRootsAsync);

        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.IsExpanded = true;
        SetExpanded(view, "A");
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));
        await cut.InvokeAsync(() => view.LoadMoreChildrenAsync(aNode));
        Assert.Equal(4, aNode.Children.Count);

        var xNode = aNode.Children.First(c => c.Id == "X");
        xNode.IsExpanded = true;
        SetExpanded(view, "X");

        ds.RemoveChild("A", "X");
        ds.LoadCallCounts.Clear();

        // === REAL PRODUCTION ReloadLevelAsync(parent) ===
        await cut.InvokeAsync(() => view.ReloadLevelAsync(aNode));

        Assert.Equal(4, aNode.Children.Count);
        Assert.DoesNotContain(aNode.Children, c => c.Id == "X");

        var aCalls = ds.LoadCallCounts.GetValueOrDefault("A");
        Assert.True(aCalls <= 2, $"Expected ≤2 loads for A (boundary=4), got {aCalls}");
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 4 — deleted child stops at boundary
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reload_DeletedExpandedChild_StopsAtPreviousBoundary()
    {
        var ds = new FakePagingDataSource(2);
        ds.AddChild(null, "A", hasChildren: true);
        for (var i = 1; i <= 20; i++)
            ds.AddChild("A", i == 3 ? "X" : $"C{i}");

        var (view, cut) = CreateView(ds, levelPageSize: 2);
        await cut.InvokeAsync(view.LoadRootsAsync);

        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.IsExpanded = true;
        SetExpanded(view, "A");
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));
        await cut.InvokeAsync(() => view.LoadMoreChildrenAsync(aNode));
        var xNode = aNode.Children.First(c => c.Id == "X");
        xNode.IsExpanded = true;
        SetExpanded(view, "X");

        ds.RemoveChild("A", "X");
        ds.LoadCallCounts.Clear();

        await cut.InvokeAsync(() => view.ReloadLevelAsync(aNode));

        Assert.Equal(4, aNode.Children.Count);
        Assert.DoesNotContain(aNode.Children, c => c.Id == "X");
        Assert.True(ds.LoadCallCounts.GetValueOrDefault("A") <= 2);
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 5 — subsequent LoadMore after restore
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reload_AfterRestore_LoadMoreContinuesCorrectly()
    {
        var ds = new FakePagingDataSource(2);
        ds.AddChild(null, "A", hasChildren: true);
        ds.AddChild("A", "A1"); ds.AddChild("A", "A2");
        ds.AddChild("A", "A3"); ds.AddChild("A", "A4");
        ds.AddChild("A", "A5"); ds.AddChild("A", "A6");

        var (view, cut) = CreateView(ds, levelPageSize: 2);
        await cut.InvokeAsync(view.LoadRootsAsync);

        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.IsExpanded = true;
        SetExpanded(view, "A");
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));
        await cut.InvokeAsync(() => view.LoadMoreChildrenAsync(aNode));
        Assert.Equal(4, aNode.Children.Count);
        // A3 on page2, expanded
        var a3pre = aNode.Children.First(c => c.Id == "A3");
        a3pre.IsExpanded = true;
        SetExpanded(view, "A3");

        ds.LoadCallCounts.Clear();

        await cut.InvokeAsync(() => view.ReloadLevelAsync(aNode));
        Assert.Equal(4, aNode.Children.Count);

        // === REAL PRODUCTION LoadMoreChildrenAsync ===
        await cut.InvokeAsync(() => view.LoadMoreChildrenAsync(aNode));

        Assert.Equal(6, aNode.Children.Count);
        Assert.Equal(new[] { "A1", "A2", "A3", "A4", "A5", "A6" },
            aNode.Children.Select(c => c.Id));
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 6 — multiple expanded, один paging pass
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reload_MultipleExpandedChildren_LoadsEachPageOnce()
    {
        var ds = new FakePagingDataSource(2);
        ds.AddChild(null, "A", hasChildren: true);
        ds.AddChild("A", "A1"); ds.AddChild("A", "A2");
        ds.AddChild("A", "A3", hasChildren: true);
        ds.AddChild("A", "A4");
        ds.AddChild("A", "A5", hasChildren: true);
        ds.AddChild("A", "A6");

        var (view, cut) = CreateView(ds, levelPageSize: 2);
        await cut.InvokeAsync(view.LoadRootsAsync);

        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        aNode.IsExpanded = true;
        SetExpanded(view, "A");
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(aNode));
        await cut.InvokeAsync(() => view.LoadMoreChildrenAsync(aNode));
        Assert.Equal(4, aNode.Children.Count);
        var a3 = aNode.Children.First(c => c.Id == "A3");
        a3.IsExpanded = true;
        SetExpanded(view, "A3");

        ds.LoadCallCounts.Clear();

        // Both A3 (page2) must be restored. Only 2 loads: page1 + page2
        await cut.InvokeAsync(() => view.ReloadLevelAsync(aNode));

        Assert.Equal(4, aNode.Children.Count);
        Assert.Contains(aNode.Children, c => c.Id == "A3" && c.IsExpanded);

        // A5 is on page3, not in boundary (4 children) → not restored
        Assert.True(ds.LoadCallCounts.GetValueOrDefault("A") <= 2);
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 7 — paging disabled regression
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reload_PagingDisabled_PreservesExpandedState()
    {
        var ds = new FakePagingDataSource(0);
        ds.AddChild(null, "A", hasChildren: true);
        ds.AddChild("A", "A1"); ds.AddChild("A", "A2");
        ds.AddChild("A", "A3", hasChildren: true);
        ds.AddChild("A3", "X");

        var (view, cut) = CreateView(ds, levelPageSize: 0);
        await cut.InvokeAsync(view.LoadRootsAsync);

        var aNode = ((IReadOnlyList<ClayTreeNode>)view.RootNodes)[0];
        await cut.InvokeAsync(() => view.ExpandNodeAsync(aNode.Id));
        var a3 = aNode.Children.First(c => c.Id == "A3");
        a3.IsExpanded = true;
        a3.HasChildren = true;
        SetExpanded(view, "A3");
        await cut.InvokeAsync(() => view.EnsureChildrenLoadedAsync(a3));

        ds.LoadCallCounts.Clear();
        await cut.InvokeAsync(() => view.ReloadLevelAsync(aNode));

        Assert.Equal(3, aNode.Children.Count);
        Assert.Contains(aNode.Children, c => c.Id == "A3" && c.IsExpanded);
    }

    // ═══════════════════════════════════════════════════════════════

    private static void SetExpanded(ClayTreeView view, string id)
    {
        if (typeof(ClayTreeView).GetField("_expanded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view) is HashSet<string> set)
            set.Add(id);
    }
}
