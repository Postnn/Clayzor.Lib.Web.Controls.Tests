using System.Text.Json;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Components.Tree.State;
using Microsoft.Extensions.Options;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты сериализации состояния дерева <see cref="ClayTreeState"/>.
/// </summary>
public class ClayTreeStateTests
{
    /// <summary>Round-trip: пустое состояние.</summary>
    [Fact]
    public void RoundTrip_Empty()
    {
        var state = new ClayTreeState();
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<ClayTreeState>(json);

        Assert.NotNull(restored);
        Assert.Null(restored!.LastExpandedId);
        Assert.Empty(restored.SelectedIds);
    }

    /// <summary>Round-trip: один выделенный элемент.</summary>
    [Fact]
    public void RoundTrip_SingleSelected()
    {
        var state = new ClayTreeState
        {
            LastExpandedId = "node-42",
            SelectedIds = ["428"],
        };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<ClayTreeState>(json);

        Assert.NotNull(restored);
        Assert.Equal("node-42", restored!.LastExpandedId);
        Assert.Single(restored.SelectedIds);
        Assert.Contains("428", restored.SelectedIds);
    }

    /// <summary>Round-trip: два выделенных элемента (задел под Multiple).</summary>
    [Fact]
    public void RoundTrip_TwoSelected()
    {
        var state = new ClayTreeState
        {
            LastExpandedId = "root",
            SelectedIds = ["111", "222"],
        };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<ClayTreeState>(json);

        Assert.NotNull(restored);
        Assert.Equal("root", restored!.LastExpandedId);
        Assert.Equal(2, restored.SelectedIds.Count);
        Assert.Contains("111", restored.SelectedIds);
        Assert.Contains("222", restored.SelectedIds);
    }

    /// <summary>Round-trip: только якорь, без выделения.</summary>
    [Fact]
    public void RoundTrip_AnchorOnly()
    {
        var state = new ClayTreeState
        {
            LastExpandedId = "anchor-1",
        };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<ClayTreeState>(json);

        Assert.NotNull(restored);
        Assert.Equal("anchor-1", restored!.LastExpandedId);
        Assert.Empty(restored.SelectedIds);
    }

    /// <summary>Round-trip: только выделение, без якоря.</summary>
    [Fact]
    public void RoundTrip_SelectedOnly()
    {
        var state = new ClayTreeState
        {
            SelectedIds = ["999"],
        };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<ClayTreeState>(json);

        Assert.NotNull(restored);
        Assert.Null(restored!.LastExpandedId);
        Assert.Single(restored.SelectedIds);
    }

    // ── TA5: StableHash и BuildParamNames ────────────────────────────────────────

    /// <summary>StableHash("") — фиксированный снапшот.</summary>
    [Fact]
    public void StableHash_Empty_ReturnsExpected()
    {
        var hash = ClaySqlTreeStateStore.StableHash("");
        Assert.Equal(2166136261u, hash);
    }

    /// <summary>StableHash("Tree1") — фиксированный снапшот.</summary>
    [Fact]
    public void StableHash_Tree1_ReturnsExpected()
    {
        var hash = ClaySqlTreeStateStore.StableHash("Tree1");
        // FNV-1a: T(84)^2166136261=2166136209 *16777619=...
        Assert.NotEqual(0u, hash);
        // Снапшот: падает при любой смене алгоритма
        Assert.Equal(4225741964u, hash);
    }

    /// <summary>BuildParamNames возвращает одинаковую пару при двух вызовах.</summary>
    [Fact]
    public void BuildParamNames_SameInput_SameOutput()
    {
        var store = CreateStore("TS_");
        var (a1, s1) = store.BuildParamNames("MyTree");
        var (a2, s2) = store.BuildParamNames("MyTree");
        Assert.Equal(a1, a2);
        Assert.Equal(s1, s2);
    }

    /// <summary>anchor != sel.</summary>
    [Fact]
    public void BuildParamNames_AnchorNotEqualSel()
    {
        var store = CreateStore("TS_");
        var (a, s) = store.BuildParamNames("Tree1");
        Assert.NotEqual(a, s);
    }

    /// <summary>Обе строки ≤ 20 при префиксах разной длины.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("T")]
    [InlineData("TS_")]
    [InlineData("VeryLongPrefix_")]
    [InlineData("ExtremelyLongPrefixThatExceedsLimit")]
    public void BuildParamNames_MaxLength20(string prefix)
    {
        var store = CreateStore(prefix);
        var (a, s) = store.BuildParamNames("Tree1");
        Assert.True(a.Length <= 20, $"anchor '{a}' length {a.Length} > 20");
        Assert.True(s.Length <= 20, $"sel '{s}' length {s.Length} > 20");
    }

    /// <summary>anchor и sel всегда различны даже при длинном префиксе.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("VeryLongPrefix_")]
    [InlineData("ExtremelyLongPrefixThatExceedsLimit")]
    public void BuildParamNames_AlwaysDistinct(string prefix)
    {
        var store = CreateStore(prefix);
        var (a, s) = store.BuildParamNames("Tree1");
        Assert.NotEqual(a, s);
    }

    private static ClaySqlTreeStateStore CreateStore(string prefix)
    {
        var gridSettings = Options.Create(new ClayGridDynamicSettings());
        var treeSettings = Options.Create(new ClayTreeDynamicSettings { StateParamPrefix = prefix });
        return new ClaySqlTreeStateStore(null!, gridSettings, treeSettings, null!);
    }
}
