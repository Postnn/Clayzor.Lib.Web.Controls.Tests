using Clayzor.Lib.Entities.DynamicGrid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты класса данных пользовательских параметров <see cref="ClayGridUserParamsData"/>.
/// </summary>
public class UserParamsTests
{
    private static ClayGridSchemaMap DefaultSchema => new();

    /// <summary>BuildParamName("flt", 140) == "flt140".</summary>
    [Theory]
    [InlineData("flt", 140, "flt140")]
    [InlineData("cols", 1, "cols1")]
    [InlineData("pgs", 99, "pgs99")]
    [InlineData("srt", 0, "srt0")]
    public void BuildParamName(string prefix, int gridId, string expected)
    {
        Assert.Equal(expected, ClayGridUserParamsData.BuildParamName(prefix, gridId));
    }

    /// <summary>BuildInsertSql содержит INSERT INTO и НЕ содержит UPDATE/MERGE.</summary>
    [Fact]
    public void BuildInsertSql_ContainsInsert_NoUpdateOrMerge()
    {
        var s = DefaultSchema;
        var sql = ClayGridUserParamsData.BuildInsertSql("MyParams", s);

        Assert.Contains("INSERT INTO", sql);
        Assert.DoesNotContain("UPDATE", sql);
        Assert.DoesNotContain("MERGE", sql);
        Assert.Contains("@clid", sql);
        Assert.Contains("@name", sql);
        Assert.Contains("@value", sql);
    }

    /// <summary>BuildLoadSql содержит IN, @clid и имена параметров @n0,@n1.</summary>
    [Fact]
    public void BuildLoadSql_ContainsInClause_AndClientIdParam()
    {
        var s = DefaultSchema;
        var sql = ClayGridUserParamsData.BuildLoadSql("MyParams", s, 2);

        Assert.Contains("@clid", sql);
        Assert.Contains("IN (@n0, @n1)", sql);
        Assert.Contains("[Параметр]", sql);
        Assert.Contains("[Значение]", sql);
        Assert.Contains("[КодНастройкиКлиента]", sql);
    }

    /// <summary>BuildLoadSql с одним именем — IN (@n0).</summary>
    [Fact]
    public void BuildLoadSql_SingleName_SingleParam()
    {
        var s = DefaultSchema;
        var sql = ClayGridUserParamsData.BuildLoadSql("T", s, 1);

        Assert.Contains("IN (@n0)", sql);
    }

    /// <summary>BuildLoadSql с нулём имён — IN ().</summary>
    [Fact]
    public void BuildLoadSql_ZeroNames_EmptyInClause()
    {
        var s = DefaultSchema;
        var sql = ClayGridUserParamsData.BuildLoadSql("T", s, 0);

        Assert.Contains("IN ()", sql);
    }

    // ── SH4: ClayGridParamRegistry ──────────────────────────────────────────

    /// <summary>GetGridParamNames для gridId=140 возвращает ровно 6 имён со стандартными префиксами.</summary>
    [Fact]
    public void GetGridParamNames_ReturnsSixNames()
    {
        var settings = new ClayGridDynamicSettings();
        var names = ClayGridParamRegistry.GetGridParamNames(settings, 140);

        Assert.Equal(6, names.Count);
        Assert.Contains("cols140", names);
        Assert.Contains("flt140", names);
        Assert.Contains("grp140", names);
        Assert.Contains("srt140", names);
        Assert.Contains("pgs140", names);
        Assert.Contains("qks140", names);
    }

    /// <summary>GetGridParamNames для двух разных gridId возвращает непересекающиеся множества.</summary>
    [Fact]
    public void GetGridParamNames_DifferentGrids_NoOverlap()
    {
        var settings = new ClayGridDynamicSettings();
        var names140 = ClayGridParamRegistry.GetGridParamNames(settings, 140);
        var names141 = ClayGridParamRegistry.GetGridParamNames(settings, 141);

        Assert.Empty(names140.Intersect(names141));
    }

    /// <summary>Все имена из GetGridParamNames укладываются в 20 символов при большом gridId.</summary>
    [Fact]
    public void GetGridParamNames_AllNamesWithin20Chars()
    {
        var settings = new ClayGridDynamicSettings();
        var names = ClayGridParamRegistry.GetGridParamNames(settings, 9999999);

        foreach (var name in names)
            Assert.True(name.Length <= 20, $"Имя \"{name}\" длиннее 20 символов: {name.Length}");
    }

    /// <summary>GetGridParamNames с длинным префиксом бросает InvalidOperationException с именем свойства.</summary>
    [Fact]
    public void GetGridParamNames_LongPrefix_ThrowsWithPropertyName()
    {
        var settings = new ClayGridDynamicSettings
        {
            ColumnsParamPrefix = "verylongprefix" // 14 chars + "9999999" = 21 chars > 20
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClayGridParamRegistry.GetGridParamNames(settings, 9999999));

        Assert.Contains("ClayGridDynamicSettings.ColumnsParamPrefix", ex.Message);
    }
}
