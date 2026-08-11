using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.DataSources;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Behavioral tests paging restore после mutation reload (CTFR2.3).
/// Использует FakePagingDataSource с контролируемыми страницами и счётчиком запросов.
/// </summary>
public class ClayTreePagingRestoreTests
{
    /// <summary>
    /// In-memory data source с paging по PageSize. Каждый узел знает своих детей.
    /// </summary>
    private sealed class FakePagingDataSource : IClayTreeDataSource
    {
        private readonly Dictionary<string, List<FakeNode>> _byParent;
        private static readonly string RootKey = "__ROOT__";
        private readonly int _pageSize;

        public FakePagingDataSource(int pageSize)
        {
            _pageSize = pageSize;
            _byParent = new Dictionary<string, List<FakeNode>>();
        }

        /// <summary>Счётчик вызовов LoadLevelAsync на каждый parent.</summary>
        public Dictionary<string, int> LoadCallCounts { get; } = new();

        public void AddChild(string? parentId, string id, bool hasChildren = false)
        {
            var key = parentId ?? RootKey;
            if (!_byParent.ContainsKey(key))
                _byParent[key] = [];
            _byParent[key].Add(new FakeNode(id, hasChildren));
        }

        public Task<ClayTreeLoadResult> LoadLevelAsync(ClayTreeLoadRequest request, CancellationToken ct = default)
        {
            var parentKey = request.Parent?.Id ?? RootKey;
            LoadCallCounts[parentKey] = LoadCallCounts.GetValueOrDefault(parentKey) + 1;

            var allChildren = _byParent.GetValueOrDefault(parentKey);
            if (allChildren is null || allChildren.Count == 0)
                return Task.FromResult(new ClayTreeLoadResult([]));

            // Симуляция keyset paging по индексу (эмулирует Left-курсор).
            var cursor = request.Parent?.LastChildCursor;
            var startIndex = cursor is long l ? (int)l : 0;

            var page = allChildren
                .Skip(startIndex)
                .Take(_pageSize + 1) // +1 sentinel
                .Select((n, i) => new ClayTreeNode
                {
                    Id = n.Id,
                    Text = n.Id,
                    Left = startIndex + i + 1,
                    HasChildren = n.HasChildren,
                })
                .ToList();

            if (page.Count > _pageSize)
            {
                page.RemoveAt(page.Count - 1); // drop sentinel
                var lastLeft = page[^1].Left;
                return Task.FromResult(new ClayTreeLoadResult(page, null, true, lastLeft));
            }

            return Task.FromResult(new ClayTreeLoadResult(page));
        }

        private record FakeNode(string Id, bool HasChildren);
    }

    /// <summary>
    /// Глубокий page 2 restore: Root → A(expanded, 4 children) → A3(expanded, позиция 3, page 2).
    /// PageSize = 2. После root reload A3 должен быть восстановлен через LoadMore.
    /// </summary>
    [Fact]
    public async Task DeepPageTwoRestore_LoadsMissingChild()
    {
        var ds = new FakePagingDataSource(2);

        // Root → A(expanded, 4 children: A1, A2, A3, A4)
        ds.AddChild(null, "A", hasChildren: true);
        ds.AddChild("A", "A1");
        ds.AddChild("A", "A2");
        ds.AddChild("A", "A3", hasChildren: true);
        ds.AddChild("A", "A4");
        ds.AddChild("A3", "X");

        // Build tree: manually load root + expand A
        var rootResult = await ds.LoadLevelAsync(new ClayTreeLoadRequest(null));
        var rootNode = rootResult.Nodes[0]; // A
        rootNode.IsExpanded = true;
        rootNode.HasChildren = true;

        // Load A's children (page 1: A1, A2; has more)
        rootNode.IsLoaded = false;
        rootNode.Children.Clear();
        rootNode.LoadedAllChildren = true;
        rootNode.LastChildCursor = null;
        var page1 = await ds.LoadLevelAsync(new ClayTreeLoadRequest(rootNode));
        rootNode.Children.AddRange(page1.Nodes);
        rootNode.IsLoaded = true;
        rootNode.LoadedAllChildren = !page1.HasMore;
        rootNode.LastChildCursor = page1.NextCursor;

        // Load page 2 (A3, A4) — user manual LoadMore
        var page2 = await ds.LoadLevelAsync(new ClayTreeLoadRequest(rootNode));
        rootNode.Children.AddRange(page2.Nodes);
        rootNode.LoadedAllChildren = !page2.HasMore;
        rootNode.LastChildCursor = page2.NextCursor;
        Assert.Equal(4, rootNode.Children.Count); // A1, A2, A3, A4

        // Expand A3
        var a3Node = rootNode.Children.First(c => c.Id == "A3");
        a3Node.IsExpanded = true;
        a3Node.Children.Clear();
        a3Node.LoadedAllChildren = true;
        a3Node.LastChildCursor = null;
        var a3Page = await ds.LoadLevelAsync(new ClayTreeLoadRequest(a3Node));
        a3Node.Children.AddRange(a3Page.Nodes);
        a3Node.IsLoaded = true;
        a3Node.LoadedAllChildren = !a3Page.HasMore;
        a3Node.LastChildCursor = a3Page.NextCursor;

        // Now simulate ReloadLevelAsync(null) — collect snapshot before clearing
        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();

        if (rootNode.IsExpanded)
        {
            snapshot[rootNode.Id] = null;
            ClayTreeView.CollectExpandedSnapshot(rootNode, snapshot, boundary);
        }

        // Verify boundary collected for ALL levels (the CTFR2.3 fix)
        Assert.True(boundary.ContainsKey("A"));
        Assert.True(boundary.ContainsKey("A3"));
        Assert.Equal(4, boundary["A"]);     // A had 4 children loaded
        Assert.Equal(1, boundary["A3"]);    // A3 had 1 child loaded

        // Reset call counts for reload simulation
        ds.LoadCallCounts.Clear();

        // Simulate: LoadRootsAsync loads root, then restore starts
        var newRootResult = await ds.LoadLevelAsync(new ClayTreeLoadRequest(null));
        var newRoot = newRootResult.Nodes[0]; // fresh A
        newRoot.IsExpanded = true;
        newRoot.HasChildren = true;

        // EnsureChildrenLoadedAsync(A) — page 1
        newRoot.Children.Clear();
        newRoot.LoadedAllChildren = true;
        newRoot.LastChildCursor = null;
        var newPage1 = await ds.LoadLevelAsync(new ClayTreeLoadRequest(newRoot));
        newRoot.Children.AddRange(newPage1.Nodes);
        newRoot.IsLoaded = true;
        newRoot.LoadedAllChildren = !newPage1.HasMore;
        newRoot.LastChildCursor = newPage1.NextCursor;

        Assert.Equal(2, newRoot.Children.Count); // A1, A2 only (page 1)

        // Now simulate what RestoreExpandedAsync does: check missing, page forward
        var parentId = newRoot.Id;
        var neededIds = snapshot.Where(kvp => kvp.Value == parentId)
                                .Select(kvp => kvp.Key).ToHashSet();
        var missing = new HashSet<string>(neededIds.Where(id => !newRoot.Children.Any(c => c.Id == id)));
        Assert.Contains("A3", missing);

        var maxChildren = boundary.GetValueOrDefault(parentId, 0);
        Assert.Equal(4, maxChildren);

        // Bounded paging — should load page 2 because Children.Count(2) < maxChildren(4)
        while (missing.Count > 0 && !newRoot.LoadedAllChildren && newRoot.Children.Count < maxChildren)
        {
            var moreResult = await ds.LoadLevelAsync(new ClayTreeLoadRequest(newRoot));
            newRoot.Children.AddRange(moreResult.Nodes);
            newRoot.LoadedAllChildren = !moreResult.HasMore;
            newRoot.LastChildCursor = moreResult.NextCursor;
            missing.RemoveWhere(id => newRoot.Children.Any(c => c.Id == id));
        }

        // A3 is now found
        Assert.DoesNotContain("A3", missing);
        Assert.Equal(4, newRoot.Children.Count);

        // LoadMore was called exactly once for A (page 1 via Ensure + page 2 via LoadMore)
        Assert.Equal(2, ds.LoadCallCounts.GetValueOrDefault("A"));
    }
}
