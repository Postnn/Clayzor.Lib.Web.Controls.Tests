using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты сохранения/восстановления раскрытости при reload дерева (CTFR2 / CTFR2.1).
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
    /// CollectExpandedSnapshot собирает childId→parentId для раскрытых потомков.
    /// Дерево: A → A1 (expanded) → A2 (expanded). B → B1 (expanded).
    /// Ожидается: A1→A, A2→A1, B1→B.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_CapturesAllLevels()
    {
        var a2 = Node("A2", expanded: true, hasChildren: false);
        var a1 = Node("A1", expanded: true, children: [a2]);
        var a = Node("A", children: [a1]);

        var b1 = Node("B1", expanded: true, hasChildren: false);
        var b = Node("B", children: [b1]);

        var snapshot = new Dictionary<string, string>();
        ClayTreeView.CollectExpandedSnapshot(a, snapshot);
        ClayTreeView.CollectExpandedSnapshot(b, snapshot);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("A", snapshot["A1"]);
        Assert.Equal("A1", snapshot["A2"]);
        Assert.Equal("B", snapshot["B1"]);
    }

    /// <summary>
    /// Свёрнутая ветка не попадает в snapshot.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_CollapsedBranch_Skipped()
    {
        var collapsed = Node("X", expanded: false, children:
        [
            Node("X1", expanded: true, children: [Node("X2", expanded: false, hasChildren: false)]),
        ]);
        var root = Node("R", children: [collapsed]);

        var snapshot = new Dictionary<string, string>();
        ClayTreeView.CollectExpandedSnapshot(root, snapshot);

        // X не раскрыт → CollectExpandedSnapshot не заходит в X.Children.
        Assert.Empty(snapshot);
    }

    /// <summary>
    /// Пустое дерево — без исключений.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_EmptyChildren_NoException()
    {
        var leaf = Node("L", expanded: false, hasChildren: false);
        var snapshot = new Dictionary<string, string>();
        ClayTreeView.CollectExpandedSnapshot(leaf, snapshot);
        Assert.Empty(snapshot);
    }

    /// <summary>
    /// Глубоко вложенное дерево: все уровни раскрыты.
    /// A → B (expanded) → C (expanded) → D (expanded).
    /// Ожидается: B→A, C→B, D→C.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_DeepChain_AllCollected()
    {
        var d = Node("D", expanded: true, hasChildren: false);
        var c = Node("C", expanded: true, children: [d]);
        var b = Node("B", expanded: true, children: [c]);
        var a = Node("A", children: [b]);

        var snapshot = new Dictionary<string, string>();
        ClayTreeView.CollectExpandedSnapshot(a, snapshot);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("A", snapshot["B"]);
        Assert.Equal("B", snapshot["C"]);
        Assert.Equal("C", snapshot["D"]);
    }

    /// <summary>
    /// Раскрытый лист попадает в snapshot (IsExpanded=true), parentId корректен.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_ExpandedLeaf_ParentRecorded()
    {
        var leaf = Node("L", expanded: true, hasChildren: false);
        var root = Node("R", children: [leaf]);

        var snapshot = new Dictionary<string, string>();
        ClayTreeView.CollectExpandedSnapshot(root, snapshot);

        Assert.Single(snapshot);
        Assert.Equal("R", snapshot["L"]);
    }

    /// <summary>
    /// Две независимые раскрытые root-ветки глубиной ≥2.
    /// Симуляция корневого сбора: для раскрытых корней добавляем маркер "".
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

        var snapshot = new Dictionary<string, string>();
        foreach (var root in new[] { rootA, rootB })
        {
            if (root.IsExpanded)
            {
                snapshot[root.Id] = ""; // маркер корня (как в ReloadLevelAsync)
                ClayTreeView.CollectExpandedSnapshot(root, snapshot);
            }
        }

        Assert.Equal(4, snapshot.Count);
        Assert.Equal("", snapshot["A"]);
        Assert.Equal("A", snapshot["A1"]);
        Assert.Equal("", snapshot["B"]);
        Assert.Equal("B", snapshot["B1"]);
    }

    /// <summary>
    /// Только один root раскрыт из двух — в snapshot только его ветка.
    /// </summary>
    [Fact]
    public void CollectExpandedSnapshot_OneOfTwoRootsExpanded_OnlyExpandedCollected()
    {
        var a1 = Node("A1", expanded: false, hasChildren: false);
        var rootA = Node("A", expanded: true, children: [a1]);
        var rootB = Node("B", expanded: false, children: [Node("B1")]);

        var snapshot = new Dictionary<string, string>();
        foreach (var root in new[] { rootA, rootB })
        {
            if (root.IsExpanded)
            {
                snapshot[root.Id] = "";
                ClayTreeView.CollectExpandedSnapshot(root, snapshot);
            }
        }

        Assert.Single(snapshot);
        Assert.Equal("", snapshot["A"]);
    }
}
