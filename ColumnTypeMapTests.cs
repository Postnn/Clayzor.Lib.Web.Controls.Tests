using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.ColumnTypes;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты маппинга типа колонки (<see cref="ClayColumnTypeMap"/>),
/// дефолтного оператора и разбора формата (<see cref="ClayColumnFormat"/>).
/// </summary>
public class ColumnTypeMapTests
{
    /// <summary>Resolve(1) → Number.</summary>
    [Theory]
    [InlineData(1, ColumnType.Number)]
    [InlineData(2, ColumnType.Text)]
    [InlineData(3, ColumnType.Date)]
    [InlineData(7, ColumnType.Boolean)]
    public void Resolve_CorrectKind(int type, ColumnType expectedKind)
    {
        var d = ClayColumnTypeMap.Resolve(type);
        Assert.NotNull(d);
        Assert.Equal(expectedKind, d!.Kind);
    }

    /// <summary>Resolve для поддерживаемых типов (1,2,3,4,5,7) не null.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public void Resolve_SupportedTypes_ReturnsDescriptor(int type)
    {
        var d = ClayColumnTypeMap.Resolve(type);
        Assert.NotNull(d);
    }

    /// <summary>Resolve для неподдержанных типов (6,8–13) возвращает null.</summary>
    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Resolve_UnsupportedTypes_ReturnsNull(int type)
    {
        Assert.Null(ClayColumnTypeMap.Resolve(type));
    }

    /// <summary>IsSupported(5) == true.</summary>
    [Fact]
    public void IsSupported_5_ReturnsTrue()
    {
        Assert.True(ClayColumnTypeMap.IsSupported(5));
    }

    /// <summary>IsSupported(1) == true.</summary>
    [Fact]
    public void IsSupported_1_ReturnsTrue()
    {
        Assert.True(ClayColumnTypeMap.IsSupported(1));
    }

    /// <summary>Дефолтный оператор Text = Contains.</summary>
    [Fact]
    public void DefaultOperator_Text_IsContains()
    {
        var d = ClayColumnTypeMap.Resolve(2)!;
        Assert.Equal(ColumnFilterOperator.Contains, d.DefaultOperator);
    }

    /// <summary>Дефолтный оператор Number = Equals.</summary>
    [Fact]
    public void DefaultOperator_Number_IsEquals()
    {
        var d = ClayColumnTypeMap.Resolve(1)!;
        Assert.Equal(ColumnFilterOperator.Equals, d.DefaultOperator);
    }

    /// <summary>Дефолтный оператор Date = Equals.</summary>
    [Fact]
    public void DefaultOperator_Date_IsEquals()
    {
        var d = ClayColumnTypeMap.Resolve(3)!;
        Assert.Equal(ColumnFilterOperator.Equals, d.DefaultOperator);
    }

    /// <summary>Дефолтный оператор Bool = Equals.</summary>
    [Fact]
    public void DefaultOperator_Bool_IsEquals()
    {
        var d = ClayColumnTypeMap.Resolve(7)!;
        Assert.Equal(ColumnFilterOperator.Equals, d.DefaultOperator);
    }

    /// <summary>Parse формата: Number "N2" → "N2".</summary>
    [Theory]
    [InlineData(1, "N2", "N2")]
    [InlineData(3, "dd.MM.yyyy", "dd.MM.yyyy")]
    [InlineData(7, "Активно=1", "Активно=1")]
    [InlineData(2, null, null)]
    [InlineData(2, "", "")]
    [InlineData(5, "SELECT ...", "SELECT ...")]
    public void Parse_Format(int kind, string? input, string? expected)
    {
        Assert.Equal(expected, ClayColumnFormat.Parse(kind, input));
    }
}
