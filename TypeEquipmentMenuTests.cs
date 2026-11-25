using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class TypeEquipmentMenuTests
{
    private const string TypeColumn = "КодТипаОборудования";
    private static readonly ClayGridDynamicContext Context = new(87, TypeColumn, 27, "codetype");

    [Theory]
    [InlineData("ru", "Computer", "Компьютер")]
    [InlineData("en", "Computer", "Computer")]
    [InlineData(null, "Computer", "Компьютер")]
    [InlineData("en", "", "Компьютер")]
    public void MenuUsesTheEmployeeLanguageAndRussianFallback(string? language, string english, string expected)
    {
        var type = new EquipmentType { Name = "Компьютер", NameEn = english };
        Assert.Equal(expected, type.GetMenuName(new Employee { Language = language }));
    }

    [Fact]
    public async Task ReadReturnsTheCompleteCatalogAndPropagatesSqlFailure()
    {
        var rows = Enumerable.Range(1, 1001).Select(id => new Dictionary<string, object?> { ["Id"] = id }).ToArray();
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(rows), Script.Error(new InvalidOperationException("failed"))]));
        using var db = new DbManager("Server=test", null, () => connection);

        Assert.Equal(1001, (await EquipmentType.ReadMenuAsync(db)).Count());
        Assert.Contains("ORDER BY КодТипаОборудования", connection.CommandLog[0]);
        Assert.DoesNotContain("TOP", connection.CommandLog[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EquipmentType.ReadMenuAsync(db));
    }

    /// <summary>Меню передаёт в SQL доверенные фильтр, параметры и порядок.</summary>
    [Fact]
    public async Task ReadMenuAppliesTrustedFilterAndOrder()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows([])]));
        using var db = new DbManager("Server=test", null, () => connection);
        var parameters = new DynamicParameters();
        parameters.Add("name", "%Компьютер%");

        await EquipmentType.ReadMenuAsync(db, default, "ТипОборудования LIKE @name", parameters, "ТипОборудования DESC");

        Assert.Contains("WHERE ТипОборудования LIKE @name", connection.CommandLog[0]);
        Assert.Contains("ORDER BY ТипОборудования DESC", connection.CommandLog[0]);
        Assert.Equal("%Компьютер%", connection.ParameterLog[0]["name"]);
    }

    [Fact]
    public void MandatoryTypeStaysAfterClearAndOverridesAStaleTypeInTheUserFilter()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TypeColumn, "PN", "CPU" };
        var userFilter = new ClayFilterGroupNode
        {
            Logic = LogicalOperator.Or,
            Nodes =
            [
                new ColumnFilter { Column = TypeColumn, Operator = ColumnFilterOperator.Equals, Value = 123 },
                new ColumnFilter { Column = "PN", Operator = ColumnFilterOperator.Equals, Value = "a" },
                new ColumnFilter { Column = "CPU", Operator = ColumnFilterOperator.Equals, Value = "b" },
            ],
        };
        var parameters = new DynamicParameters();
        var where = ClayGrid<object>.BuildDynamicFilter(userFilter, Context, parameters, known);

        Assert.NotNull(where);
        Assert.Contains("AND", where);
        Assert.Contains("OR", where);
        Assert.Single(where.Split(TypeColumn, StringSplitOptions.None).Skip(1));
        Assert.Contains(parameters.ParameterNames, key => Equals(parameters.Get<object?>(key), 27));
        Assert.DoesNotContain(parameters.ParameterNames, key => Equals(parameters.Get<object?>(key), 123));
        Assert.Equal(3, userFilter.Nodes.Count);

        var reset = new DynamicParameters();
        Assert.Contains(TypeColumn, ClayGrid<object>.BuildDynamicFilter(null, Context, reset, known));
        Assert.Equal(27, reset.Get<int>(Assert.Single(reset.ParameterNames)));
        Assert.Throws<InvalidOperationException>(() => ClayGrid<object>.BuildDynamicFilter(null, Context, new(), new HashSet<string>()));
    }

    [Fact]
    public void CreateUrlUsesSelectedTypeInsteadOfEitherConfiguredDefault()
    {
        var url = new Uri(ClayGrid<object>.BuildContextCreateUrl(
            new Uri("http://localhost/Equipment/ModelEquipmentForm?codetype=1&_codetype=2&keep=1#form"), Context));
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("27", query["codetype"].ToString());
        Assert.False(query.ContainsKey("_codetype"));
        Assert.Equal("1", query["keep"].ToString());
        Assert.Equal("#form", url.Fragment);
    }

    [Fact]
    public void UrlFilterResolvesTheMetadataAliasAndKeepsSavedDefaults()
    {
        var root = new ClayFilterGroupNode();
        var columns = new Dictionary<string, string> { ["model"] = "МодельОборудования", ["PN"] = "PN" };
        var saved = new Dictionary<string, string> { ["PN"] = "existing" };
        ClayGridUrlFilterParser.Apply(root,
            [new("model", ColumnFilterOperator.Contains, "test", false, true),
             new("PN", ColumnFilterOperator.Equals, "other", true, false)], saved, columns);

        var filter = Assert.IsType<ColumnFilter>(Assert.Single(root.Nodes));
        Assert.Equal("МодельОборудования", filter.Column);
        Assert.Equal("test", filter.Value);
    }
}
