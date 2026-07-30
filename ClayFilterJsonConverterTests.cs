using System.Text.Json;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Filter;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты JSON-конвертера <see cref="ClayFilterJsonConverter"/>:
/// полиморфный round-trip дерева фильтра с дискриминатором $type.
/// </summary>
public class ClayFilterJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static string Serialize(IClayFilterNode node)
        => JsonSerializer.Serialize(node, Options);

    private static IClayFilterNode? Deserialize(string json)
        => JsonSerializer.Deserialize<IClayFilterNode>(json, Options);

    /// <summary>Группа с листом → JSON → десериализация → структура сохранена.</summary>
    [Fact]
    public void RoundTrip_GroupWithLeaf_StructurePreserved()
    {
        var root = new ClayFilterGroupNode { Logic = LogicalOperator.And };
        root.Nodes.Add(new ColumnFilter { Column = "col", Operator = ColumnFilterOperator.Contains, Value = "test", Source = ClayFilterSource.CompositeDialog });

        var json = Serialize(root);
        var restored = Deserialize(json);

        Assert.NotNull(restored);
        var group = Assert.IsType<ClayFilterGroupNode>(restored);
        Assert.Equal(LogicalOperator.And, group.Logic);
        Assert.Single(group.Nodes);
        var leaf = Assert.IsType<ColumnFilter>(group.Nodes[0]);
        Assert.Equal("col", leaf.Column);
        Assert.Equal(ColumnFilterOperator.Contains, leaf.Operator);
        Assert.Equal("test", leaf.Value);
    }

    /// <summary>ParamName и SecondParamName не сериализуются (помечены [JsonIgnore]).</summary>
    [Fact]
    public void Serialize_ParamName_NotInJson()
    {
        var leaf = new ColumnFilter
        {
            Column = "col", Operator = ColumnFilterOperator.Equals, Value = 1,
            ParamName = "secret", SecondParamName = "also_secret",
            Source = ClayFilterSource.CompositeDialog,
        };

        var json = Serialize(leaf);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("paramName", json);
        Assert.DoesNotContain("secondParamName", json);
    }

    /// <summary>IsNew не сериализуется (транзиентный UI-флаг).</summary>
    [Fact]
    public void Serialize_IsNew_NotInJson()
    {
        var leaf = new ColumnFilter { Column = "col", Operator = ColumnFilterOperator.Equals, Value = 1, IsNew = true };

        var json = Serialize(leaf);
        Assert.DoesNotContain("isNew", json);
    }

    /// <summary>ValueFilter round-trip — Negate, BlankChecked, Values сохранены.</summary>
    [Fact]
    public void RoundTrip_ValueFilter_PreservesValues()
    {
        var vf = new ValueFilter { Column = "col", Values = ["a", "b", "c"], Negate = true, BlankChecked = true };

        var json = Serialize(vf);
        var restored = Deserialize(json);

        Assert.NotNull(restored);
        var restoredVf = Assert.IsType<ValueFilter>(restored);
        Assert.Equal("col", restoredVf.Column);
        Assert.True(restoredVf.Negate);
        Assert.True(restoredVf.BlankChecked);
        Assert.Equal(3, restoredVf.Values.Count);
    }

    /// <summary>Второе условие (SecondClause) — round-trip с LogicalOperator и SecondValue.</summary>
    [Fact]
    public void RoundTrip_SecondClause_Preserved()
    {
        var leaf = new ColumnFilter
        {
            Column = "col", Operator = ColumnFilterOperator.GreaterThan, Value = 10,
            LogicalOperator = LogicalOperator.And,
            SecondOperator = ColumnFilterOperator.LessThan, SecondValue = 100,
            Source = ClayFilterSource.CompositeDialog,
        };

        var json = Serialize(leaf);
        var restored = Deserialize(json);

        Assert.NotNull(restored);
        var restoredLeaf = Assert.IsType<ColumnFilter>(restored);
        Assert.Equal(ColumnFilterOperator.GreaterThan, restoredLeaf.Operator);
        Assert.Equal(ColumnFilterOperator.LessThan, restoredLeaf.SecondOperator);
        Assert.Equal(100, restoredLeaf.SecondValue);
    }
}
