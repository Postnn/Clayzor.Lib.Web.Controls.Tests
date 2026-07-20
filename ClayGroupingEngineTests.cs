using Clayzor.Lib.Web.Controls.Components.Grid;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты <see cref="ClayGroupingEngine.BuildInterleavedHeaders"/> (GN4) и её согласованности
/// с <see cref="ClayGroupingEngine.BuildAggregates"/> (GN2) по формату FullKey.
/// </summary>
public class ClayGroupingEngineTests
{
    // Разделитель уровней FullKey — тот же символ (0x1F), что и в ClayGroupingEngine.
    // Заведён числовым способом, а не escape-литералом, чтобы избежать путаницы с текстом теста.
    private static readonly string Sep = new([(char)0x1F]);

    /// <summary>Рекурсивно собирает FullKey → ItemCount из дерева групп (аналог CollectCounts).</summary>
    private static void CollectCounts(List<GridGroupNode> nodes, Dictionary<string, int> lookup)
    {
        foreach (var node in nodes)
        {
            lookup[node.Aggregate.FullKey] = node.Aggregate.ItemCount;
            CollectCounts(node.Children, lookup);
        }
    }

    /// <summary>Первая строка: заголовки для всех уровней.</summary>
    [Fact]
    public void BuildInterleavedHeaders_FirstRow_ReturnsAllLevels()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", "b"], null, new Dictionary<string, int>());

        Assert.Equal(2, headers.Count);
        Assert.Equal(0, headers[0].Depth);
        Assert.Equal("a", headers[0].FullKey);
        Assert.Equal(1, headers[1].Depth);
        Assert.Equal("a" + Sep + "b", headers[1].FullKey);
    }

    /// <summary>Та же группа подряд — заголовков нет.</summary>
    [Fact]
    public void BuildInterleavedHeaders_SameGroup_ReturnsEmpty()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", "b"], ["a", "b"], new Dictionary<string, int>());
        Assert.Empty(headers);
    }

    /// <summary>Сменился только внутренний уровень — один заголовок на глубине 1.</summary>
    [Fact]
    public void BuildInterleavedHeaders_InnerLevelChanged_ReturnsOneHeader()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", "c"], ["a", "b"], new Dictionary<string, int>());

        Assert.Single(headers);
        Assert.Equal(1, headers[0].Depth);
        Assert.Equal("a" + Sep + "c", headers[0].FullKey);
    }

    /// <summary>Сменился внешний уровень — заголовки на обеих глубинах.</summary>
    [Fact]
    public void BuildInterleavedHeaders_OuterLevelChanged_ReturnsBothHeaders()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["x", "y"], ["a", "b"], new Dictionary<string, int>());

        Assert.Equal(2, headers.Count);
        Assert.Equal(0, headers[0].Depth);
        Assert.Equal(1, headers[1].Depth);
    }

    /// <summary>Пять уровней, сменился третий (индекс 2) — заголовки для глубин 2,3,4.</summary>
    [Fact]
    public void BuildInterleavedHeaders_FiveLevels_ThirdChanged_ReturnsThreeHeaders()
    {
        string?[] previous = ["a", "b", "c", "d", "e"];
        string?[] current  = ["a", "b", "x", "d", "e"];

        var headers = ClayGroupingEngine.BuildInterleavedHeaders(current, previous, new Dictionary<string, int>());

        Assert.Equal(3, headers.Count);
        Assert.Equal([2, 3, 4], headers.Select(h => h.Depth));
    }

    /// <summary>NULL-ключ на последнем уровне: подпись — EmptyGroupDisplay, пустой сегмент FullKey.</summary>
    [Fact]
    public void BuildInterleavedHeaders_NullKey_UsesEmptyGroupDisplay()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", null], null, new Dictionary<string, int>());

        var last = headers[^1];
        Assert.Equal(1, last.Depth);
        Assert.Equal(ClayGroupingEngine.EmptyGroupDisplay, last.DisplayValue);
        Assert.Equal("a" + Sep, last.FullKey);
    }

    /// <summary>NULL и NULL на одном уровне подряд — одна и та же группа, заголовков нет.</summary>
    [Fact]
    public void BuildInterleavedHeaders_NullThenNull_ReturnsEmpty()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", null], ["a", null], new Dictionary<string, int>());
        Assert.Empty(headers);
    }

    /// <summary>
    /// NULL и пустая строка неразличимы на этом уровне (известное ограничение, см. GN3) —
    /// смена ключа не детектируется.
    /// </summary>
    [Fact]
    public void BuildInterleavedHeaders_NullThenEmptyString_ReturnsEmpty()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", ""], ["a", null], new Dictionary<string, int>());
        Assert.Empty(headers);
    }

    /// <summary>Отсутствующий FullKey в countLookup — ItemCount 0, без исключения.</summary>
    [Fact]
    public void BuildInterleavedHeaders_MissingCountLookupKey_ReturnsZero()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", "b"], null, new Dictionary<string, int>());
        Assert.All(headers, h => Assert.Equal(0, h.ItemCount));
    }

    /// <summary>GroupKeys никогда не содержит null, даже когда currentKeys содержит null.</summary>
    [Fact]
    public void BuildInterleavedHeaders_GroupKeys_NeverContainsNull()
    {
        var headers = ClayGroupingEngine.BuildInterleavedHeaders(["a", null, "c"], null, new Dictionary<string, int>());
        Assert.All(headers, h => Assert.DoesNotContain(null, h.GroupKeys));
    }

    /// <summary>
    /// Главный тест шага: FullKey, построенный BuildInterleavedHeaders, обязан совпадать
    /// с FullKey из BuildAggregates → BuildTree → ComputeParentCounts → CollectCounts,
    /// а ItemCount заголовков — со значениями из дерева.
    /// </summary>
    [Fact]
    public void BuildInterleavedHeaders_MatchesEngineCountLookup()
    {
        var groupRows = new List<GridGroupRow>
        {
            new() { Keys = ["a", "b", "c1"], Cnt = 2 },
            new() { Keys = ["a", "b", "c2"], Cnt = 3 },
            new() { Keys = ["a", "b2", "c3"], Cnt = 1 },
        };

        var aggregates = ClayGroupingEngine.BuildAggregates(groupRows);
        var roots      = ClayGroupingEngine.BuildTree(aggregates);
        ClayGroupingEngine.ComputeParentCounts(roots);

        var countLookup = new Dictionary<string, int>();
        CollectCounts(roots, countLookup);

        string?[]? previous = null;
        var allHeaders = new List<GroupHeaderRow>();
        foreach (string?[] current in new[]
                 {
                     new string?[] { "a", "b", "c1" },
                     ["a", "b", "c2"],
                     ["a", "b2", "c3"],
                 })
        {
            allHeaders.AddRange(ClayGroupingEngine.BuildInterleavedHeaders(current, previous, countLookup));
            previous = current;
        }

        Assert.All(allHeaders, h => Assert.True(countLookup.ContainsKey(h.FullKey)));

        Assert.Equal(6, countLookup["a"]);
        Assert.Equal(5, countLookup["a" + Sep + "b"]);
        Assert.Equal(2, countLookup["a" + Sep + "b" + Sep + "c1"]);

        foreach (var h in allHeaders)
            Assert.Equal(countLookup[h.FullKey], h.ItemCount);
    }

    /// <summary>Та же проверка согласованности, но с NULL в ключах.</summary>
    [Fact]
    public void BuildInterleavedHeaders_MatchesEngineCountLookup_WithNullKeys()
    {
        var groupRows = new List<GridGroupRow>
        {
            new() { Keys = ["a", null, "c1"], Cnt = 2 },
            new() { Keys = ["a", "b", "c2"], Cnt = 3 },
        };

        var aggregates = ClayGroupingEngine.BuildAggregates(groupRows);
        var roots      = ClayGroupingEngine.BuildTree(aggregates);
        ClayGroupingEngine.ComputeParentCounts(roots);

        var countLookup = new Dictionary<string, int>();
        CollectCounts(roots, countLookup);

        string?[]? previous = null;
        var allHeaders = new List<GroupHeaderRow>();
        foreach (string?[] current in new[]
                 {
                     new string?[] { "a", null, "c1" },
                     ["a", "b", "c2"],
                 })
        {
            allHeaders.AddRange(ClayGroupingEngine.BuildInterleavedHeaders(current, previous, countLookup));
            previous = current;
        }

        Assert.All(allHeaders, h => Assert.True(countLookup.ContainsKey(h.FullKey)));
        foreach (var h in allHeaders)
            Assert.Equal(countLookup[h.FullKey], h.ItemCount);
    }
}
