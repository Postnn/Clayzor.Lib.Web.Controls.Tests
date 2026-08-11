using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты сохранения/восстановления раскрытости при reload дерева (CTFR2).
/// </summary>
public class ClayTreeReloadExpandedTests
{
    /// <summary>
    /// Создаёт тестовый узел с детьми. Id = уровень.вложенность.индекс.
    /// </summary>
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
    /// CollectExpandedIds собирает раскрытых потомков рекурсивно — только тех,
    /// кто находится в цепочке раскрытых узлов (IsExpanded = true).
    /// Дерево: A → A1 (expanded) → A2 (expanded). B → B1 (expanded).
    /// Ожидается: A1, A2, B1.
    /// </summary>
    [Fact]
    public void CollectExpandedIds_CapturesAllLevels()
    {
        var a2 = Node("A2", expanded: true, hasChildren: false);
        var a1 = Node("A1", expanded: true, children: [a2]);
        var a = Node("A", children: [a1]);

        var b1 = Node("B1", expanded: true, hasChildren: false);
        var b = Node("B", children: [b1]);

        var ids = new HashSet<string>();
        ClayTreeView.CollectExpandedIds(a, ids);
        ClayTreeView.CollectExpandedIds(b, ids);

        Assert.Equal(3, ids.Count);
        Assert.Contains("A1", ids);
        Assert.Contains("A2", ids);
        Assert.Contains("B1", ids);
    }

    /// <summary>
    /// Свёрнутая ветка не попадает в snapshot — только раскрытые узлы и их потомки.
    /// </summary>
    [Fact]
    public void CollectExpandedIds_CollapsedBranch_Skipped()
    {
        var collapsed = Node("X", expanded: false, children:
        [
            Node("X1", expanded: true, children: [Node("X2", expanded: false, hasChildren: false)]),
        ]);
        var root = Node("R", children: [collapsed]);

        var ids = new HashSet<string>();
        ClayTreeView.CollectExpandedIds(root, ids);

        // X1 раскрыт, но X не раскрыт → CollectExpandedIds не заходит в X.Children.
        Assert.Empty(ids);
    }

    /// <summary>
    /// Пустое дерево — без исключений.
    /// </summary>
    [Fact]
    public void CollectExpandedIds_EmptyChildren_NoException()
    {
        var leaf = Node("L", expanded: false, hasChildren: false);
        var ids = new HashSet<string>();
        ClayTreeView.CollectExpandedIds(leaf, ids);
        Assert.Empty(ids);
    }

    /// <summary>
    /// Глубоко вложенное дерево: все уровни раскрыты — все попадают в snapshot.
    /// A → B (expanded) → C (expanded) → D (expanded). Ожидается: B, C, D (3 узла).
    /// Корень A не входит — CollectExpandedIds обходит детей, не сам узел.
    /// </summary>
    [Fact]
    public void CollectExpandedIds_DeepChain_AllCollected()
    {
        var d = Node("D", expanded: true, hasChildren: false);
        var c = Node("C", expanded: true, children: [d]);
        var b = Node("B", expanded: true, children: [c]);
        var a = Node("A", expanded: true, children: [b]);

        var ids = new HashSet<string>();
        ClayTreeView.CollectExpandedIds(a, ids);

        Assert.Equal(3, ids.Count);
        Assert.Contains("B", ids);
        Assert.Contains("C", ids);
        Assert.Contains("D", ids);
    }

    /// <summary>
    /// Ветка без HasChildren: раскрытый лист собирается (IsExpanded=true), но рекурсия
    /// останавливается — детей нет.
    /// </summary>
    [Fact]
    public void CollectExpandedIds_ExpandedLeaf_CollectedButNoRecursion()
    {
        var leaf = Node("L", expanded: true, hasChildren: false);
        var root = Node("R", children: [leaf]);

        var ids = new HashSet<string>();
        ClayTreeView.CollectExpandedIds(root, ids);

        // Раскрытый лист попадает в snapshot — он expanded.
        // Рекурсия в него не заходит (детей нет), исключений нет.
        Assert.Single(ids);
        Assert.Contains("L", ids);
    }

    /// <summary>
    /// Две независимые раскрытые root-ветки глубиной ≥2: все уровни собраны.
    /// Моделирует сценарий из спеки: A→A1→A2 и B→B1→B2.
    /// </summary>
    [Fact]
    public void CollectExpandedIds_TwoDeepRootBranches_AllCollected()
    {
        var a2 = Node("A2", expanded: false, hasChildren: false);
        var a1 = Node("A1", expanded: true, children: [a2]);
        var rootA = Node("A", expanded: true, children: [a1]);

        var b2 = Node("B2", expanded: false, hasChildren: false);
        var b1 = Node("B1", expanded: true, children: [b2]);
        var rootB = Node("B", expanded: true, children: [b1]);

        var ids = new HashSet<string>();
        // Симуляция корневого сбора: для каждого раскрытого корня добавляем его Id и обходим.
        foreach (var root in new[] { rootA, rootB })
        {
            if (root.IsExpanded)
            {
                ids.Add(root.Id);
                ClayTreeView.CollectExpandedIds(root, ids);
            }
        }

        Assert.Equal(4, ids.Count);
        Assert.Contains("A", ids);
        Assert.Contains("A1", ids);
        Assert.Contains("B", ids);
        Assert.Contains("B1", ids);
    }

    /// <summary>
    /// Только один root раскрыт из двух — в snapshot только его ветка.
    /// </summary>
    [Fact]
    public void CollectExpandedIds_OneOfTwoRootsExpanded_OnlyExpandedCollected()
    {
        var a1 = Node("A1", expanded: false, hasChildren: false);
        var rootA = Node("A", expanded: true, children: [a1]);
        var rootB = Node("B", expanded: false, children: [Node("B1")]);

        var ids = new HashSet<string>();
        foreach (var root in new[] { rootA, rootB })
        {
            if (root.IsExpanded)
            {
                ids.Add(root.Id);
                ClayTreeView.CollectExpandedIds(root, ids);
            }
        }

        Assert.Single(ids);
        Assert.Contains("A", ids);
    }
}
