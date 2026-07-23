using Clayzor.Lib.Entities.DynamicGrid;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты метода <see cref="ClayGrid{TEntity}.ComputeEffectiveQuickSearchColumns"/> —
/// итоговый набор колонок быстрого поиска из трёх источников.
/// </summary>
public class QuickSearchEffectiveTests
{
    private static ClayColumnDefinition Col(string column, int type, bool quickSearch = false)
        => new(0, 0, column, null, null, 1, null, type, quickSearch);

    /// <summary>Колонки нет в таблице → пусто.</summary>
    [Fact]
    public void NoColumn_ReturnsEmpty()
    {
        var cols = new[] { Col("Название", 2, true) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(false, cols, null);
        Assert.Empty(result);
    }

    /// <summary>Все 0/NULL, нет строки пользователя → пусто.</summary>
    [Fact]
    public void AllAdminFalse_NoUser_ReturnsEmpty()
    {
        var cols = new[] { Col("Название", 2, false), Col("Код", 1, false) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(true, cols, null);
        Assert.Empty(result);
    }

    /// <summary>Название=1, нет строки пользователя → админский набор.</summary>
    [Fact]
    public void AdminTrue_NoUser_ReturnsAdminSet()
    {
        var cols = new[] { Col("Название", 2, true), Col("Код", 1, false) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(true, cols, null);
        Assert.Equal(new[] { "Название" }, result);
    }

    /// <summary>Название=1, пользовательская пустая строка → перебивает → пусто.</summary>
    [Fact]
    public void AdminTrue_UserEmpty_ReturnsEmpty()
    {
        var cols = new[] { Col("Название", 2, true) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(true, cols, "");
        Assert.Empty(result);
    }

    /// <summary>Название=1, пользователь выбрал КодИсследования → пользовательский набор.</summary>
    [Fact]
    public void AdminTrue_UserSelectsOther_ReturnsUserSet()
    {
        var cols = new[] { Col("Название", 2, true), Col("КодИсследования", 1, true) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(true, cols, "КодИсследования");
        Assert.Equal(new[] { "КодИсследования" }, result);
    }

    /// <summary>Админ поставил 1 только справочнику (тип 5) → фильтр типа исключает → пусто.</summary>
    [Fact]
    public void AdminTrue_LookupType_FilteredOut()
    {
        var cols = new[] { Col("КодТипа", 5, true) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(true, cols, null);
        Assert.Empty(result);
    }

    /// <summary>Пользователь выбрал Название + КодТипа (справочник) → справочник исключён.</summary>
    [Fact]
    public void UserSelectsWithLookup_LookupFilteredOut()
    {
        var cols = new[] { Col("Название", 2, true), Col("КодТипа", 5, true) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(true, cols, "Название,КодТипа");
        Assert.Equal(new[] { "Название" }, result);
    }

    /// <summary>Пользователь выбрал несуществующую колонку → игнорируется → пусто.</summary>
    [Fact]
    public void UserSelectsUnknown_ReturnsEmpty()
    {
        var cols = new[] { Col("Название", 2, true) };
        var result = ClayGrid<object>.ComputeEffectiveQuickSearchColumns(true, cols, "НетТакойКолонки");
        Assert.Empty(result);
    }
}
