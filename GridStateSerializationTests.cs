using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Clayzor.Lib.Web.Controls.Components.Grid.Filter;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты round-trip сериализации состояния динамического грида
/// <see cref="GridStateSerializer"/>.
/// </summary>
public class GridStateSerializationTests
{
    /// <summary>Round-trip колонок: сериализация→десериализация сохраняет SqlName:Visible.</summary>
    [Fact]
    public void Columns_RoundTrip_PreservesOrderAndVisibility()
    {
        var meta1 = new ClayColumnMeta { ColumnId = 1, SqlName = "colA", DisplayName = "A" };
        var meta2 = new ClayColumnMeta { ColumnId = 2, SqlName = "colB", DisplayName = "B" };
        var columnOrder = new List<int> { 1, 2 };
        var columnById  = new Dictionary<int, ClayColumnMeta> { [1] = meta1, [2] = meta2 };
        var hidden       = new HashSet<string> { "colB" };

        var serialized = GridStateSerializer.SerializeColumns(columnOrder, columnById, hidden);
        var deserialized = GridStateSerializer.DeserializeColumns(serialized);

        Assert.Equal(2, deserialized.Count);
        Assert.Equal(("colA", 1), deserialized[0]);
        Assert.Equal(("colB", 0), deserialized[1]);
    }

    /// <summary>Round-trip сортировки.</summary>
    [Fact]
    public void Sort_RoundTrip_PreservesColumnsAndDirection()
    {
        var sort = new List<SortColumn>
        {
            new("colA", false),
            new("colB", true)
        };

        var serialized   = GridStateSerializer.SerializeSort(sort);
        var deserialized = GridStateSerializer.DeserializeSort(serialized);

        Assert.Equal(2, deserialized.Count);
        Assert.Equal("colA", deserialized[0].Column);
        Assert.False(deserialized[0].Desc);
        Assert.Equal("colB", deserialized[1].Column);
        Assert.True(deserialized[1].Desc);
    }

    /// <summary>Round-trip группировки.</summary>
    [Fact]
    public void Groups_RoundTrip_PreservesList()
    {
        var groups = new List<string> { "colA", "colB" };

        var serialized   = GridStateSerializer.SerializeGroups(groups);
        var deserialized = GridStateSerializer.DeserializeGroups(serialized);

        Assert.Equal(2, deserialized.Count);
        Assert.Equal("colA", deserialized[0]);
        Assert.Equal("colB", deserialized[1]);
    }

    /// <summary>Round-trip размера страницы.</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(10)]
    public void PageSize_RoundTrip_PreservesValue(int pageSize)
    {
        var serialized   = GridStateSerializer.SerializePageSize(pageSize);
        var deserialized = GridStateSerializer.DeserializePageSize(serialized);

        Assert.Equal(pageSize, deserialized);
    }

    /// <summary>Фильтр: пустая группа → null.</summary>
    [Fact]
    public void Filter_EmptyGroup_SerializesToNull()
    {
        var root = new ClayFilterGroupNode();
        Assert.Null(GridStateSerializer.SerializeFilter(root));
    }

    /// <summary>Фильтр: round-trip с одним ColumnFilter.</summary>
    [Fact]
    public void Filter_RoundTrip_WithLeaf()
    {
        var root = new ClayFilterGroupNode();
        root.Nodes.Add(new ColumnFilter
        {
            Column   = "Name",
            Operator = ColumnFilterOperator.Contains,
            Value    = "test"
        });

        var json         = GridStateSerializer.SerializeFilter(root);
        Assert.NotNull(json);

        var deserialized = GridStateSerializer.DeserializeFilter(json);
        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.Nodes);

        var leaf = (ColumnFilter)deserialized.Nodes[0];
        Assert.Equal("Name", leaf.Column);
        Assert.Equal(ColumnFilterOperator.Contains, leaf.Operator);
        Assert.Equal("test", leaf.Value);
    }

    /// <summary>Десериализация null/пусто → null.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Deserialize_EmptyOrNull_ReturnsNull(string? input)
    {
        Assert.Null(GridStateSerializer.DeserializeFilter(input));
        Assert.Empty(GridStateSerializer.DeserializeColumns(input));
        Assert.Empty(GridStateSerializer.DeserializeSort(input));
        Assert.Empty(GridStateSerializer.DeserializeGroups(input));
        Assert.Null(GridStateSerializer.DeserializePageSize(input));
    }
}
