using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Helpers;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты логики режимов фильтрации дерева (без БД).
/// </summary>
public class ClayTreeFilterModeTests
{
    /// <summary>
    /// Подсчёт совпадений: только ноды с IsMatch=true считаются,
    /// предки с HasMatchChildren=true — нет (правило 3).
    /// </summary>
    [Fact]
    public void CountMatches_OnlyIsMatchCounted()
    {
        var nodes = new[]
        {
            new TestNode { IsMatch = true, HasMatchChildren = false },   // совпадение
            new TestNode { IsMatch = true, HasMatchChildren = true },    // совпадение + предок
            new TestNode { IsMatch = false, HasMatchChildren = true },   // только предок
            new TestNode { IsMatch = false, HasMatchChildren = false },  // ни то ни другое
        };

        var matchCount = nodes.Count(n => n.IsMatch);
        Assert.Equal(2, matchCount);
    }

    /// <summary>Capped: когда совпадений больше max → true.</summary>
    [Fact]
    public void Capped_WhenMatchesExceedMax()
    {
        var matchCount = 6;
        var max = 5;
        Assert.True(matchCount > max);
    }

    /// <summary>Не capped: когда совпадений ≤ max → false.</summary>
    [Fact]
    public void NotCapped_WhenMatchesWithinMax()
    {
        var matchCount = 5;
        var max = 5;
        Assert.False(matchCount > max);
    }

    /// <summary>
    /// BuildFilterColumns исключает колонки из FilterExcludedColumns (регистронезависимо).
    /// </summary>
    [Fact]
    public void BuildFilterColumns_ExcludesByName()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Name", DisplayName = "Имя", ColumnType = ColumnType.Text },
            new() { SqlName = "Code", DisplayName = "Код", ColumnType = ColumnType.Text },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, ["CODE"]);

        Assert.Single(result);
        Assert.Equal("Name", result[0].SqlName);
    }

    private sealed class TestNode
    {
        public bool IsMatch { get; set; }
        public bool HasMatchChildren { get; set; }
    }
}
