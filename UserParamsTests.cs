using Clayzor.Lib.Entities.DynamicGrid;

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
}
