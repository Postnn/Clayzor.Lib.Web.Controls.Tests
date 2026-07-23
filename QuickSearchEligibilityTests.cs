using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты метода <see cref="ClayColumnKindExtensions.SupportsQuickSearch"/> —
/// белый список типов, допустимых для быстрого поиска.
/// </summary>
public class QuickSearchEligibilityTests
{
    /// <summary>Допустимые типы: 1,2,3,4,10,12,13.</summary>
    [Theory]
    [InlineData(1, true)]   // Number
    [InlineData(2, true)]   // Text
    [InlineData(3, true)]   // Date
    [InlineData(4, true)]   // Link
    [InlineData(10, true)]  // DateTimeLocal
    [InlineData(12, true)]  // LimitedText
    [InlineData(13, true)]  // TimeLocal
    public void SupportsQuickSearch_EligibleTypes(int kind, bool expected)
    {
        Assert.Equal(expected, ClayColumnKindExtensions.SupportsQuickSearch(kind));
    }

    /// <summary>Недопустимые типы: 5,6,7,8,9,11.</summary>
    [Theory]
    [InlineData(5)]   // List — код в SQL, текст из подзапроса
    [InlineData(6)]   // ConditionBool — фильтр-онли
    [InlineData(7)]   // Bool — чекбокс, нет текста
    [InlineData(8)]   // Html — теги искажают поиск
    [InlineData(9)]   // Icon — код в SQL, иконка из подзапроса
    [InlineData(11)]  // ConditionList — фильтр-онли
    public void SupportsQuickSearch_NonEligibleTypes(int kind)
    {
        Assert.False(ClayColumnKindExtensions.SupportsQuickSearch(kind));
    }

    /// <summary>Недопустимый тип перебивает флаг УчаствуетВБыстромПоиске=1.
    /// Справочник (List=5) никогда не участвует в поиске.</summary>
    [Fact]
    public void SupportsQuickSearch_ListType_AlwaysFalse()
    {
        Assert.False(ClayColumnKindExtensions.SupportsQuickSearch((int)ClayColumnKind.List));
    }

    /// <summary>Неизвестный код типа → false, без исключения.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(99)]
    public void SupportsQuickSearch_UnknownKind_False(int kind)
    {
        Assert.False(ClayColumnKindExtensions.SupportsQuickSearch(kind));
    }
}
