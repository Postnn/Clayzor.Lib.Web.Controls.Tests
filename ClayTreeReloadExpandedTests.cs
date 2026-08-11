using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты сохранения/восстановления раскрытости при reload дерева (CTFR2–CTFR2.3).
/// </summary>
public class ClayTreeReloadExpandedTests
{
    private static ClayTreeNode Node(string id, bool expanded = false, bool hasChildren = true,
        List<ClayTreeNode>? children = null)
    {
        return new ClayTreeNode
        {
            Id = id,
            Text = id,
            IsExpanded = expanded,
            HasChildren = hasChildren,
            Children = children ?? [],
        };
    }

    /// <summary>
    /// CollectExpandedSnapshot собирает childId→parentId (string?) + paging boundary.
    /// Дерево: A → A1 (expanded) → A2 (expanded). B → B1 (expanded).
    /// Ожидается: A1→A, A2→A1, B1→B. Boundary: A→1, A1→1, B→1, B1→0.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_CapturesAllLevels()
    {
        var a2 = Node("A2", expanded: true, hasChildren: false);
        var a1 = Node("A1", expanded: true, children: [a2]);
        var a = Node("A", children: [a1]);

        var b1 = Node("B1", expanded: true, hasChildren: false);
        var b = Node("B", children: [b1]);

        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();
        ClayTreeView.CollectExpandedSnapshot(a, snapshot, boundary);
        ClayTreeView.CollectExpandedSnapshot(b, snapshot, boundary);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("A", snapshot["A1"]);
        Assert.Equal("A1", snapshot["A2"]);
        Assert.Equal("B", snapshot["B1"]);
    }

    /// <summary>
    /// Свёрнутая ветка не попадает в snapshot и не имеет boundary.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_CollapsedBranch_Skipped()
    {
        var collapsed = Node("X", expanded: false, children:
        [
            Node("X1", expanded: true, children: [Node("X2", expanded: false, hasChildren: false)]),
        ]);
        var root = Node("R", children: [collapsed]);

        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();
        ClayTreeView.CollectExpandedSnapshot(root, snapshot, boundary);

        // X не раскрыт → не заходим в X.Children. Только boundary root.
        Assert.Empty(snapshot);
        Assert.True(boundary.ContainsKey("R"));
    }

    /// <summary>
    /// Пустое дерево — без исключений.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_EmptyChildren_NoException()
    {
        var leaf = Node("L", expanded: false, hasChildren: false);
        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();
        ClayTreeView.CollectExpandedSnapshot(leaf, snapshot, boundary);
        Assert.Empty(snapshot);
        Assert.Equal(0, boundary["L"]);
    }

    /// <summary>
    /// Глубоко вложенное дерево: все уровни раскрыты.
    /// A → B (expanded) → C (expanded) → D (expanded).
    /// Ожидается: B→A, C→B, D→C. Boundary: A→1, B→1, C→1, D→0.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_DeepChain_AllCollected()
    {
        var d = Node("D", expanded: true, hasChildren: false);
        var c = Node("C", expanded: true, children: [d]);
        var b = Node("B", expanded: true, children: [c]);
        var a = Node("A", children: [b]);

        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();
        ClayTreeView.CollectExpandedSnapshot(a, snapshot, boundary);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("A", snapshot["B"]);
        Assert.Equal("B", snapshot["C"]);
        Assert.Equal("C", snapshot["D"]);
    }

    /// <summary>
    /// Раскрытый лист: parentId корректен.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_ExpandedLeaf_ParentRecorded()
    {
        var leaf = Node("L", expanded: true, hasChildren: false);
        var root = Node("R", children: [leaf]);

        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();
        ClayTreeView.CollectExpandedSnapshot(root, snapshot, boundary);

        Assert.Single(snapshot);
        Assert.Equal("R", snapshot["L"]);
    }

    /// <summary>
    /// Две независимые раскрытые root-ветки. Root marker = null.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_TwoDeepRootBranches_AllCollected()
    {
        var a2 = Node("A2", expanded: false, hasChildren: false);
        var a1 = Node("A1", expanded: true, children: [a2]);
        var rootA = Node("A", expanded: true, children: [a1]);

        var b2 = Node("B2", expanded: false, hasChildren: false);
        var b1 = Node("B1", expanded: true, children: [b2]);
        var rootB = Node("B", expanded: true, children: [b1]);

        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();
        foreach (var root in new[] { rootA, rootB })
        {
            if (root.IsExpanded)
            {
                snapshot[root.Id] = null;
                ClayTreeView.CollectExpandedSnapshot(root, snapshot, boundary);
            }
        }

        Assert.Equal(4, snapshot.Count);
        Assert.Null(snapshot["A"]);
        Assert.Equal("A", snapshot["A1"]);
        Assert.Null(snapshot["B"]);
        Assert.Equal("B", snapshot["B1"]);
    }

    /// <summary>
    /// Только один root раскрыт — root marker = null.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_OneOfTwoRootsExpanded_OnlyExpandedCollected()
    {
        var a1 = Node("A1", expanded: false, hasChildren: false);
        var rootA = Node("A", expanded: true, children: [a1]);
        var rootB = Node("B", expanded: false, children: [Node("B1")]);

        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();
        foreach (var root in new[] { rootA, rootB })
        {
            if (root.IsExpanded)
            {
                snapshot[root.Id] = null;
                ClayTreeView.CollectExpandedSnapshot(root, snapshot, boundary);
            }
        }

        Assert.Single(snapshot);
        Assert.Null(snapshot["A"]);
    }

    /// <summary>
    /// CTFR2.3: paging boundary собирается рекурсивно для каждого раскрытого parent.
    /// Root(expanded, 1 child) → A(expanded, 1 child) → A3(expanded, 0 children).
    /// CollectExpandedSnapshot обходит детей — root в snapshot добавляется вызывающим кодом.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_RecursiveBoundary_AllParentsHaveBoundary()
    {
        var a3 = Node("A3", expanded: true, hasChildren: false);
        var a = Node("A", expanded: true, children: [a3]);
        var root = Node("Root", expanded: true, children: [a]);

        var snapshot = new Dictionary<string, string?>();
        var boundary = new Dictionary<string, int>();

        // Симуляция корневого сбора (как в ReloadLevelAsync):
        snapshot[root.Id] = null;
        ClayTreeView.CollectExpandedSnapshot(root, snapshot, boundary);

        // Expanded mapping: Root→null, A→Root, A3→A.
        Assert.Equal(3, snapshot.Count);
        Assert.Null(snapshot["Root"]);
        Assert.Equal("Root", snapshot["A"]);
        Assert.Equal("A", snapshot["A3"]);

        // Paging boundary собрана для ВСЕХ трёх раскрытых parent (CTFR2.3 fix).
        Assert.True(boundary.ContainsKey("Root"), "Root missing from boundary");
        Assert.True(boundary.ContainsKey("A"), "A missing from boundary");
        Assert.True(boundary.ContainsKey("A3"), "A3 missing from boundary");
        Assert.Equal(1, boundary["Root"]);
        Assert.Equal(1, boundary["A"]);
        Assert.Equal(0, boundary["A3"]);
    }
}
