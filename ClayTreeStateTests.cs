using System.Text.Json;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;

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
}
