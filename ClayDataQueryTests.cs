using Clayzor.Lib.Web.Controls.Components.Grid;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты <see cref="ClayDataQuery.BuildOrderBy"/> — белый список допустимых выражений
/// ORDER BY (GA1 — защита от инъекции через shared-ссылку).
/// </summary>
public class ClayDataQueryTests
{
    /// <summary>Без белого списка поведение не меняется (регрессия).</summary>
    [Fact]
    public void BuildOrderBy_NoWhitelist_KeepsExistingBehavior()
    {
        var q = new ClayDataQuery
        {
            SortColumns = [new SortColumn("Name", true)]
        };

        var result = q.BuildOrderBy("Id");

        Assert.Equal("Name DESC", result);
    }

    /// <summary>Инъекция отсеяна: колонка вне белого списка → возвращается defaultOrder.</summary>
    [Fact]
    public void BuildOrderBy_RejectsInjectedColumn_FallsBackToDefault()
    {
        var q = new ClayDataQuery
        {
            SortColumns = [new SortColumn("Name; DROP TABLE x --", false)]
        };
        var allowed = new HashSet<string> { "Name" };

        var result = q.BuildOrderBy("Id", allowed);

        Assert.Equal("Id", result);
    }

    /// <summary>Валидная колонка из белого списка проходит.</summary>
    [Fact]
    public void BuildOrderBy_KeepsAllowedColumn()
    {
        var q = new ClayDataQuery
        {
            SortColumns = [new SortColumn("Name", false)]
        };
        var allowed = new HashSet<string> { "Name" };

        var result = q.BuildOrderBy("Id", allowed);

        Assert.Equal("Name ASC", result);
    }

    /// <summary>Если все колонки отсеяны, ORDER BY не пуст — возвращается defaultOrder.</summary>
    [Fact]
    public void BuildOrderBy_AllFiltered_ReturnsDefaultOrder()
    {
        var q = new ClayDataQuery
        {
            SortColumns = [new SortColumn("Evil", false)]
        };
        var allowed = new HashSet<string> { "Name" };

        var result = q.BuildOrderBy("Id", allowed);

        Assert.Equal("Id", result);
    }

    /// <summary>defaultOrder — доверенная строка из определения, проверка не требуется.</summary>
    [Fact]
    public void BuildOrderBy_DefaultOrderIsTrusted()
    {
        var q = new ClayDataQuery(); // SortColumns пуст

        var result = q.BuildOrderBy("Порядок, НазваниеАнализа");

        Assert.Equal("Порядок, НазваниеАнализа", result);
    }

    /// <summary>defaultOrder не фильтруется даже при несовпадении с белым списком.</summary>
    [Fact]
    public void BuildOrderBy_DefaultOrderNotFilteredByWhitelist()
    {
        var q = new ClayDataQuery(); // SortColumns пуст
        var allowed = new HashSet<string> { "Наименование" }; // не перекрывает defaultOrder

        var result = q.BuildOrderBy("Порядок, НазваниеАнализа", allowed);

        // defaultOrder — доверенная строка; проходит как есть
        Assert.Equal("Порядок, НазваниеАнализа", result);
    }

    /// <summary>Из смеси валидных и невалидных — остаются только валидные.</summary>
    [Fact]
    public void BuildOrderBy_FiltersOnlyUnknown()
    {
        var q = new ClayDataQuery
        {
            SortColumns = [new SortColumn("Evil", false), new SortColumn("Name", true)]
        };
        var allowed = new HashSet<string> { "Name" };

        var result = q.BuildOrderBy("Id", allowed);

        Assert.Equal("Name DESC", result);
    }

    /// <summary>Группировка: инъекция в GroupColumns отсеивается.</summary>
    [Fact]
    public void BuildOrderBy_Grouped_RejectsInjectedGroupColumn()
    {
        var q = new ClayDataQuery
        {
            GroupEnabled = true,
            GroupColumns = ["Evil; DROP"],
            SortColumns  = [new SortColumn("Evil; DROP", false)]
        };
        var allowed = new HashSet<string> { "Name" };

        var result = q.BuildOrderBy("Id", allowed);

        // Обе колонки (группировки и сортировки) вне белого списка → defaultOrder
        Assert.Equal("Id", result);
    }

    /// <summary>Группировка: валидная колонка проходит.</summary>
    [Fact]
    public void BuildOrderBy_Grouped_KeepsAllowedGroupColumn()
    {
        var q = new ClayDataQuery
        {
            GroupEnabled = true,
            GroupColumns = ["Grp"],
            SortColumns  = [new SortColumn("Grp", true)]
        };
        var allowed = new HashSet<string> { "Grp" };

        var result = q.BuildOrderBy("Id", allowed);

        Assert.Equal("Grp DESC", result);
    }
}
