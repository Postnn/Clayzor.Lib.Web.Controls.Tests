using Clayzor.Lib.Entities.DynamicGrid;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты сохранения/загрузки пользовательского выбора колонок быстрого поиска.
/// </summary>
public class QuickSearchUserParamsTests
{
    /// <summary>BuildParamName("qks", 140) = "qks140" (≤ 20 символов).</summary>
    [Theory]
    [InlineData("qks", 140, "qks140")]
    [InlineData("qks", 1, "qks1")]
    [InlineData("cols", 140, "cols140")]
    public void BuildParamName_ValidKey(string prefix, int gridId, string expected)
    {
        Assert.Equal(expected, ClayGridUserParamsData.BuildParamName(prefix, gridId));
    }

    /// <summary>Ключ длиннее 20 символов → InvalidOperationException.</summary>
    [Fact]
    public void BuildParamName_TooLong_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ClayGridUserParamsData.BuildParamName("too_long_prefix_21c", 99999));
        Assert.Contains("20", ex.Message);
    }

    /// <summary>Граница 20 символов — допустимо.</summary>
    [Fact]
    public void BuildParamName_Exactly20Chars_Ok()
    {
        // prefix (12) + gridId (8) = 20
        var name = ClayGridUserParamsData.BuildParamName("123456789012", 34567890);
        Assert.Equal(20, name.Length);
    }

    /// <summary>Граница 20 символов + 1 → исключение.</summary>
    [Fact]
    public void BuildParamName_21Chars_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ClayGridUserParamsData.BuildParamName("1234567890123", 34567890));
    }
}
