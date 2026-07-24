using Clayzor.Lib.Web.Controls.Components.Grid;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты <see cref="ClayGridOptions"/>: дефолты (контракт совместимости).
/// <para>
/// Тесты дефолтов — «защёлка»: случайная правка значения по умолчанию
/// падает здесь в CI, а не у тестировщика на странице.
/// </para>
/// </summary>
public class ClayGridOptionsTests
{
    // ── Дефолты ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Фиксирует все 21 значение по умолчанию. Случайное изменение любого из них —
    /// регрессия, ломающая страницы, которые не задают параметр явно.
    /// </summary>
    [Fact]
    public void Defaults_HaveExpectedValues()
    {
        var d = new ClayGridOptions();

        // Источник данных
        Assert.Equal(string.Empty, d.SelectSql);
        Assert.NotNull(d.SearchColumns);
        Assert.Empty(d.SearchColumns);
        Assert.Equal(string.Empty, d.DefaultOrder);
        Assert.Equal(50, d.PageSize);

        // Внешний вид
        Assert.Equal("Список", d.Title);
        Assert.Equal("clay-grid", d.Id);
        Assert.True(d.ShowAddButton);
        Assert.True(d.ShowPagination);

        // Колонки
        Assert.Equal(ColumnMenuMode.Mobile, d.ColumnMenuMode);
        Assert.True(d.AllowColumnReorder);

        // Фильтрация
        Assert.True(d.EnableValueFilter);
        Assert.NotNull(d.FilterColumnTypes);
        Assert.Empty(d.FilterColumnTypes);
        Assert.Null(d.FilterLookupOptions);

        // Редактирование
        Assert.Null(d.EditDialogType);
        Assert.Equal("Запись обновлена", d.EditSuccessMessage);

        // Выбор и групповые операции
        Assert.False(d.SelectVisible);
        Assert.False(d.ShowPrint);
        Assert.False(d.ShowExcel);
        Assert.Null(d.CustomBatchGroups);

        // Динамический режим
        Assert.False(d.Dynamic);
        Assert.Null(d.DynamicGridId);
    }

    /// <summary>
    /// <see cref="ClayGridOptions.Defaults"/> возвращает тот же экземпляр
    /// при повторном обращении (синглтон).
    /// </summary>
    [Fact]
    public void Defaults_StaticProperty_ReturnsSameInstance()
    {
        var a = ClayGridOptions.Defaults;
        var b = ClayGridOptions.Defaults;
        Assert.Same(a, b);
    }

    /// <summary>
    /// <see cref="ClayGridOptions.Defaults"/> содержит те же значения,
    /// что и новый экземпляр.
    /// </summary>
    [Fact]
    public void Defaults_StaticProperty_HasSameValuesAsNewInstance()
    {
        var d = ClayGridOptions.Defaults;
        var n = new ClayGridOptions();

        Assert.Equal(n.Title, d.Title);
        Assert.Equal(n.Id, d.Id);
        Assert.Equal(n.PageSize, d.PageSize);
        Assert.Equal(n.SelectSql, d.SelectSql);
        Assert.Equal(n.DefaultOrder, d.DefaultOrder);
        Assert.Equal(n.EditSuccessMessage, d.EditSuccessMessage);
        Assert.Equal(n.ColumnMenuMode, d.ColumnMenuMode);
        Assert.Equal(n.ShowAddButton, d.ShowAddButton);
        Assert.Equal(n.ShowPagination, d.ShowPagination);
        Assert.Equal(n.AllowColumnReorder, d.AllowColumnReorder);
        Assert.Equal(n.EnableValueFilter, d.EnableValueFilter);
        Assert.Equal(n.SelectVisible, d.SelectVisible);
        Assert.Equal(n.ShowPrint, d.ShowPrint);
        Assert.Equal(n.ShowExcel, d.ShowExcel);
        Assert.Equal(n.Dynamic, d.Dynamic);
        Assert.Null(d.EditDialogType);
        Assert.Null(d.FilterLookupOptions);
        Assert.Null(d.CustomBatchGroups);
        Assert.Null(d.DynamicGridId);
    }

}
