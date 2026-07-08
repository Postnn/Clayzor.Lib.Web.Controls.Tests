using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.Filter;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты URL-хелпера <see cref="ClayFilterUrlHelper"/>:
/// сжатие/восстановление дерева фильтра через DeflateStream + Base64Url.
/// </summary>
public class ClayFilterUrlHelperTests
{
    /// <summary>Дерево → Serialize → Deserialize → структура сохранена.</summary>
    [Fact]
    public void SerializeDeserialize_RoundTrip_StructurePreserved()
    {
        var root = new ClayFilterGroupNode { Logic = LogicalOperator.Or };
        root.Nodes.Add(new ColumnFilter { Column = "col", Operator = ColumnFilterOperator.Contains, Value = "test", Source = ClayFilterSource.CompositeDialog });
        root.Nodes.Add(new ValueFilter { Column = "col2", Values = [1, 2], Negate = false });

        var url = ClayFilterUrlHelper.Serialize(root);
        Assert.NotNull(url);

        var restored = ClayFilterUrlHelper.Deserialize(url);
        Assert.NotNull(restored);
        Assert.Equal(LogicalOperator.Or, restored!.Logic);
        Assert.Equal(2, restored.Nodes.Count);
        Assert.IsType<ColumnFilter>(restored.Nodes[0]);
        Assert.IsType<ValueFilter>(restored.Nodes[1]);
    }

    /// <summary>Пустое дерево (нет дочерних узлов) → Serialize возвращает null.</summary>
    [Fact]
    public void Serialize_EmptyTree_ReturnsNull()
    {
        var empty = new ClayFilterGroupNode();
        var result = ClayFilterUrlHelper.Serialize(empty);
        Assert.Null(result);
    }

    /// <summary>null или пустая строка или невалидный Base64 → Deserialize возвращает null.</summary>
    [Fact]
    public void Deserialize_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ClayFilterUrlHelper.Deserialize(null));
        Assert.Null(ClayFilterUrlHelper.Deserialize(""));
        Assert.Null(ClayFilterUrlHelper.Deserialize("invalid_base64!!!"));
    }
}
