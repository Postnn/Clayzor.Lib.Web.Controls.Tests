using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты SQL-условия быстрого поиска: экранирование LIKE, CAST/CONVERT, форма условия.
/// </summary>
public class QuickSearchSqlTests
{
    /// <summary>Экранирование % → \%, _ → \_, [ → \[.</summary>
    [Theory]
    [InlineData("50%", @"50\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData("test[1]", @"test\[1]")]
    [InlineData("normal", "normal")]
    public void EscapeLikePattern_Escapes(string input, string expected)
    {
        Assert.Equal(expected, ClayGrid<object>.EscapeLikePattern(input));
    }

    /// <summary>Одна текстовая колонка → одно выражение без лишних OR.</summary>
    [Fact]
    public void BuildSearchLikeExpr_TextColumn()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("Название", 2, null);
        Assert.Equal(@"Название LIKE @q ESCAPE '\'", expr);
    }

    /// <summary>Числовая колонка → CAST.</summary>
    [Fact]
    public void BuildSearchLikeExpr_NumberColumn_Cast()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("КодИсследования", 1, null);
        Assert.Equal(@"CAST(КодИсследования AS nvarchar(50)) LIKE @q ESCAPE '\'", expr);
    }

    /// <summary>Дата без времени → CONVERT с форматом 104 (dd.mm.yyyy).</summary>
    [Fact]
    public void BuildSearchLikeExpr_DateColumn_Convert104()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("ДатаСоздания", 3, "dd.MM.yyyy");
        Assert.Equal(@"CONVERT(nvarchar(30), ДатаСоздания, 104) LIKE @q ESCAPE '\'", expr);
    }

    /// <summary>Дата+время → CONVERT с форматом 121 (yyyy-mm-dd hh:mi:ss).</summary>
    [Fact]
    public void BuildSearchLikeExpr_DateTimeLocal_Convert121()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("ДатаВремя", 10, "dd.MM.yyyy HH:mm");
        Assert.Equal(@"CONVERT(nvarchar(30), ДатаВремя, 121) LIKE @q ESCAPE '\'", expr);
    }

    /// <summary>Время → CONVERT с форматом 121.</summary>
    [Fact]
    public void BuildSearchLikeExpr_TimeLocal_Convert121()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("Время", 13, "HH:mm");
        Assert.Equal(@"CONVERT(nvarchar(30), Время, 121) LIKE @q ESCAPE '\'", expr);
    }

    /// <summary>Дробное (decimal) покрывается Number → CAST в nvarchar.</summary>
    [Fact]
    public void BuildSearchLikeExpr_DecimalViaNumber_Cast()
    {
        // Number = 1 покрывает int/long/decimal
        var expr = ClayGrid<object>.BuildSearchLikeExpr("Цена", 1, null);
        Assert.Equal(@"CAST(Цена AS nvarchar(50)) LIKE @q ESCAPE '\'", expr);
    }

    /// <summary>Ссылка (тип 4) — текстовое выражение без CAST.</summary>
    [Fact]
    public void BuildSearchLikeExpr_LinkColumn()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("Ссылка", 4, null);
        Assert.Equal(@"Ссылка LIKE @q ESCAPE '\'", expr);
    }

    /// <summary>В сгенерированном SQL нет подстановки значения — только @q.</summary>
    [Fact]
    public void BuildSearchLikeExpr_ParameterOnly_NoInlineValue()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("Test", 2, null);
        Assert.Contains("@q", expr);
        Assert.DoesNotContain("%Test%", expr); // значение не подставлено в SQL
    }

    /// <summary>Совместимость с 2008 R2: нет запрещённых конструкций.</summary>
    [Fact]
    public void BuildSearchLikeExpr_CompatibleWith2008R2()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("Col", 1, null);
        Assert.DoesNotContain("OFFSET", expr);
        Assert.DoesNotContain("FETCH", expr);
        Assert.DoesNotContain("TRY_CONVERT", expr);
        Assert.DoesNotContain("STRING_SPLIT", expr);
        Assert.DoesNotContain("IIF", expr);
    }

    /// <summary>Одна колонка в наборе → один LIKE, нет OR.</summary>
    [Fact]
    public void SingleColumn_NoOr()
    {
        var expr = ClayGrid<object>.BuildSearchLikeExpr("Col", 2, null);
        Assert.DoesNotContain("OR", expr);
        Assert.Contains("LIKE", expr);
    }
}
