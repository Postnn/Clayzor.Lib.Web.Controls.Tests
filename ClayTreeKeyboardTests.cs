using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.DataSources;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Components.Tree.State;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки команд общей клавиатуры в разметке и обработчиках дерева.</summary>
public sealed class ClayTreeKeyboardTests
{
    /// <summary>Активация обновляет callback карточки, клавиша отметки меняет только checked.</summary>
    [Fact]
    public async Task ActivationAndCheckRemainIndependent()
    {
        await using var context = CreateContext();
        string? current = null;
        var checkedIds = new HashSet<string>();
        var options = CreateOptions();
        options.SelectionMode = ClayTreeSelectionMode.Multiple;
        options.IsChecked = node => checkedIds.Contains(node.Id);
        options.ToggleCheckedAsync = node =>
        {
            if (!checkedIds.Add(node.Id))
                checkedIds.Remove(node.Id);
            return Task.CompletedTask;
        };
        var tree = context.Render<ClayTreeView>(p => p.Add(c => c.Options, options)
            .Add(c => c.DataSource, new TreeSource())
            .Add(c => c.OnNodeClick, node => current = node.Id));

        tree.WaitForElement("[data-clay-tree-node=a]");
        Assert.Equal("true", tree.Find("[role=tree]").GetAttribute("aria-multiselectable"));
        await tree.InvokeAsync(() => tree.Instance.InvokeKeyboardActionAsync("a", "activate"));
        Assert.Equal("a", current);
        Assert.Empty(checkedIds);
        await tree.InvokeAsync(() => tree.Instance.InvokeKeyboardActionAsync("b", "check"));
        Assert.Equal("a", current);
        Assert.Contains("b", checkedIds);
        Assert.Equal("true", tree.Find("[data-clay-tree-node=b]").GetAttribute("aria-checked"));
        Assert.Null(tree.Find("[data-clay-tree-node=b]").GetAttribute("aria-selected"));
    }

    /// <summary>Клавиша раскрытия загружает детей, после сворачивания скрытый ребёнок не активируется.</summary>
    [Fact]
    public async Task ExpandCollapseAndHiddenNodeContract()
    {
        await using var context = CreateContext();
        string? current = null;
        var tree = context.Render<ClayTreeView>(p => p.Add(c => c.Options, CreateOptions())
            .Add(c => c.DataSource, new TreeSource())
            .Add(c => c.OnNodeClick, node => current = node.Id));

        Assert.Equal("false", tree.WaitForElement("[data-clay-tree-node=a]").GetAttribute("aria-expanded"));
        await tree.InvokeAsync(() => tree.Instance.InvokeKeyboardActionAsync("a", "expand"));
        var child = tree.Find("[role=group] [role=treeitem]");
        Assert.Equal("a1", child.GetAttribute("data-clay-tree-node"));
        await tree.InvokeAsync(() => tree.Instance.InvokeKeyboardActionAsync("a1", "activate"));
        Assert.Equal("a1", current);
        Assert.Equal("true", tree.Find("[data-clay-tree-node=a1]").GetAttribute("aria-selected"));

        await tree.InvokeAsync(() => tree.Instance.InvokeKeyboardActionAsync("a", "collapse"));
        current = null;
        await tree.InvokeAsync(() => tree.Instance.InvokeKeyboardActionAsync("a1", "activate"));
        Assert.Null(current);
        Assert.Empty(tree.FindAll("[data-clay-tree-node=a1]"));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<ISqlErrorHandler, FakeSqlErrorHandler>();
        context.Services.AddSingleton(new DbManager("Server=test"));
        context.Services.AddSingleton<IClayTreeStateStore, TreeState>();
        return context;
    }

    private static ClayTreeOptions CreateOptions() => new()
    {
        TreeId = "keyboard-test",
        SelectSql = "unused",
        HierarchyMode = ClayTreeHierarchyMode.NestedSet,
        Schema = new ClayTreeSchema { IdColumn = "Id", TextColumn = "Text" },
        ShowBusyOverlay = false,
    };

    private sealed class TreeSource : IClayTreeDataSource
    {
        public Task<ClayTreeLoadResult> LoadLevelAsync(ClayTreeLoadRequest request, CancellationToken ct = default)
        {
            IReadOnlyList<ClayTreeNode> nodes = request.Parent is null
                ? [new() { Id = "a", Text = "A", HasChildren = true }, new() { Id = "b", Text = "B" }]
                : [new() { Id = "a1", Text = "A1" }];
            return Task.FromResult(new ClayTreeLoadResult(nodes));
        }
    }

    private sealed class TreeState : IClayTreeStateStore
    {
        public Task<ClayTreeState?> LoadAsync(string treeId, CancellationToken ct = default) =>
            Task.FromResult<ClayTreeState?>(null);

        public Task SaveAsync(string treeId, ClayTreeState state, CancellationToken ct = default) => Task.CompletedTask;
    }
}
