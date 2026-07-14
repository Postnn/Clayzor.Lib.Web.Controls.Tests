using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.ColumnTypes;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Clayzor.Lib.Web.Controls.Components.Grid.Filter;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты разбора URL-фильтра <see cref="ClayGridUrlFilterParser"/> — ключевой компонент G8.
/// </summary>
public class UrlFilterParserTests
{
    private static readonly TextColumnType    _text   = new();
    private static readonly NumberColumnType  _number = new();
    private static readonly DateColumnType    _date   = new();

    /// <summary>"eq~DQA1" → Equals, "DQA1", IsForced.</summary>
    [Fact]
    public void Parse_EqOperator_ParsesCorrectly()
    {
        var result = ClayGridUrlFilterParser.Parse("name", "eq~DQA1", _text);
        Assert.Equal("name", result.UrlKey);
        Assert.Equal(ColumnFilterOperator.Equals, result.Operator);
        Assert.Equal("DQA1", result.Value);
        Assert.True(result.IsForced);
        Assert.False(result.IsDefault);
    }

    /// <summary>"ge~20260101" → GreaterThanOrEqual.</summary>
    [Fact]
    public void Parse_GeOperator_ParsesCorrectly()
    {
        var result = ClayGridUrlFilterParser.Parse("created", "ge~20260101", _date);
        Assert.Equal(ColumnFilterOperator.GreaterThanOrEqual, result.Operator);
        Assert.Equal("20260101", result.Value);
    }

    /// <summary>"in~3,5" → Equals, "3,5".</summary>
    [Fact]
    public void Parse_InOperator_ParsesCorrectly()
    {
        var result = ClayGridUrlFilterParser.Parse("type", "in~3,5", _number);
        Assert.Equal(ColumnFilterOperator.Equals, result.Operator);
        Assert.Equal("3,5", result.Value);
    }

    /// <summary>"between~20260101~20260401" → Equals, value с обеими границами.</summary>
    [Fact]
    public void Parse_BetweenOperator_ParsesWithBothBounds()
    {
        var result = ClayGridUrlFilterParser.Parse("created", "between~20260101~20260401", _date);
        Assert.Equal(ColumnFilterOperator.Equals, result.Operator);
        Assert.Equal("20260101~20260401", result.Value);
    }

    /// <summary>Без "op~" → дефолтный оператор, value = вся строка (правило 5).</summary>
    [Fact]
    public void Parse_NoOperator_UsesDefaultOperator()
    {
        var result = ClayGridUrlFilterParser.Parse("created", "20260101", _date);
        Assert.Equal(ColumnFilterOperator.Equals, result.Operator);
        Assert.Equal("20260101", result.Value);
    }

    /// <summary>"_name" → IsDefault = true.</summary>
    [Fact]
    public void Parse_UnderscorePrefix_SetsIsDefault()
    {
        var result = ClayGridUrlFilterParser.Parse("_name", "eq~x", _text);
        Assert.True(result.IsDefault);
        Assert.False(result.IsForced);
        Assert.Equal("name", result.UrlKey);
    }

    /// <summary>Неизвестный оператор → дефолтный + вся строка как value.</summary>
    [Fact]
    public void Parse_UnknownOperator_TreatsAsValue()
    {
        var result = ClayGridUrlFilterParser.Parse("name", "badop~something", _text);
        Assert.Equal(ColumnFilterOperator.Contains, result.Operator); // дефолт Text = Contains
        Assert.Equal("badop~something", result.Value);
    }

    /// <summary>Apply: _key без сохранённого → условие добавлено.</summary>
    [Fact]
    public void Apply_DefaultKey_NoSaved_AddsCondition()
    {
        var root = new ClayFilterGroupNode();
        var parsed = new[] { new ParsedUrlFilter("col", ColumnFilterOperator.Equals, "val", IsDefault: true, IsForced: false) };
        var saved  = new Dictionary<string, string>();

        ClayGridUrlFilterParser.Apply(root, parsed, saved);

        Assert.Single(root.Nodes);
    }

    /// <summary>Apply: _key с сохранённым → НЕ добавлено (правило 1).</summary>
    [Fact]
    public void Apply_DefaultKey_HasSaved_SkipsCondition()
    {
        var root = new ClayFilterGroupNode();
        var parsed = new[] { new ParsedUrlFilter("col", ColumnFilterOperator.Equals, "val", IsDefault: true, IsForced: false) };
        var saved  = new Dictionary<string, string> { ["col"] = "saved_value" };

        ClayGridUrlFilterParser.Apply(root, parsed, saved);

        Assert.Empty(root.Nodes);
    }

    /// <summary>Apply: key без '_' → добавлено (правило 2, IsForced).</summary>
    [Fact]
    public void Apply_ForcedKey_AlwaysAdds()
    {
        var root = new ClayFilterGroupNode();
        var parsed = new[] { new ParsedUrlFilter("col", ColumnFilterOperator.Equals, "val", IsDefault: false, IsForced: true) };
        var saved  = new Dictionary<string, string> { ["col"] = "saved_value" };

        ClayGridUrlFilterParser.Apply(root, parsed, saved);

        Assert.Single(root.Nodes);
    }
}
