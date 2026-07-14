using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.ColumnTypes;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты сложных типов колонок: Тип 5 (Список), Тип 6 (Условие), Тип 9 (Пиктограмма), Тип 11 (Условие-список).
/// </summary>
public class ComplexColumnTypesTests
{
    /// <summary>Resolve(5) возвращает ClayListColumnType.</summary>
    [Fact]
    public void Resolve_Type5_ReturnsListDescriptor()
    {
        var desc = ClayColumnTypeMap.Resolve(5);
        Assert.NotNull(desc);
        Assert.IsType<ClayListColumnType>(desc);
    }

    /// <summary>IsSupported(5) == true.</summary>
    [Fact]
    public void IsSupported_Type5_ReturnsTrue()
    {
        Assert.True(ClayColumnTypeMap.IsSupported(5));
    }

    /// <summary>Resolve(6) возвращает ClayConditionBoolColumnType.</summary>
    [Fact]
    public void Resolve_Type6_ReturnsConditionBoolDescriptor()
    {
        var desc = ClayColumnTypeMap.Resolve(6);
        Assert.NotNull(desc);
        Assert.IsType<ClayConditionBoolColumnType>(desc);
    }

    /// <summary>ClayListColumnType.DefaultOperator = Equals.</summary>
    [Fact]
    public void ListColumnType_DefaultOperator_IsEquals()
    {
        var desc = new ClayListColumnType();
        Assert.Equal(ColumnFilterOperator.Equals, desc.DefaultOperator);
    }

    /// <summary>ClayListColumnType.Format возвращает строку как есть.</summary>
    [Fact]
    public void ListColumnType_Format_ReturnsString()
    {
        var desc = new ClayListColumnType();
        Assert.Equal("test", desc.Format("test"));
        Assert.Equal("", desc.Format(null));
    }

    /// <summary>Resolve(9) возвращает ClayIconColumnType.</summary>
    [Fact]
    public void Resolve_Type9_ReturnsIconDescriptor()
    {
        var desc = ClayColumnTypeMap.Resolve(9);
        Assert.NotNull(desc);
        Assert.IsType<ClayIconColumnType>(desc);
    }

    /// <summary>IsSupported(9) == true.</summary>
    [Fact]
    public void IsSupported_Type9_ReturnsTrue()
    {
        Assert.True(ClayColumnTypeMap.IsSupported(9));
    }

    /// <summary>ClayIconColumnType.DefaultOperator = Equals.</summary>
    [Fact]
    public void IconColumnType_DefaultOperator_IsEquals()
    {
        var desc = new ClayIconColumnType();
        Assert.Equal(ColumnFilterOperator.Equals, desc.DefaultOperator);
    }

    /// <summary>Resolve(11) возвращает ClayConditionListColumnType.</summary>
    [Fact]
    public void Resolve_Type11_ReturnsConditionListDescriptor()
    {
        var desc = ClayColumnTypeMap.Resolve(11);
        Assert.NotNull(desc);
        Assert.IsType<ClayConditionListColumnType>(desc);
    }

    /// <summary>IsSupported(11) == true.</summary>
    [Fact]
    public void IsSupported_Type11_ReturnsTrue()
    {
        Assert.True(ClayColumnTypeMap.IsSupported(11));
    }

    /// <summary>ConditionBoolColumnType.Kind = Boolean.</summary>
    [Fact]
    public void ConditionBoolColumnType_Kind_IsBoolean()
    {
        var desc = new ClayConditionBoolColumnType();
        Assert.Equal(ColumnType.Boolean, desc.Kind);
    }
}
