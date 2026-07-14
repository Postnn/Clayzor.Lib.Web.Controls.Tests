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

    /// <summary>Resolve(6) всё ещё null — будет в G12.</summary>
    [Fact]
    public void Resolve_Type6_StillNull()
    {
        Assert.Null(ClayColumnTypeMap.Resolve(6));
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
}
