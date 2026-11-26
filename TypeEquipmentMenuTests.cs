using System.Security.Claims;
using Bunit;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MudBlazor.Services;
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

    /// <summary>Устаревшие строки и ошибка не публикуются, пока следующий SQL-запрос ожидает.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededResponseDoesNotChangeTheMenuWhileLatestRequestWaits(bool fail)
    {
        var a = new ScriptEntry
        {
            Rows = MenuRows("A"),
            Exception = fail ? new InvalidOperationException("A failed") : null,
            Started = Signal(),
            Release = Signal(),
        };
        var b = new ScriptEntry { Rows = MenuRows("B"), Started = Signal(), Release = Signal() };
        using var connection = MenuConnection("en", new ScriptEntry { Rows = MenuRows("initial") }, a, b);
        await using var context = CreateMenuContext(connection);
        var menu = context.Render<TypeEquipmentMenu>();
        menu.WaitForAssertion(() => Assert.Contains("initial", menu.Markup));
        var grid = menu.FindComponent<ClayGrid<EquipmentType>>().Instance;
        var older = menu.InvokeAsync(() => grid.OnQueryChanged.InvokeAsync(new ClayDataQuery()));
        await a.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requested = Signal();
        var latest = menu.InvokeAsync(() =>
        {
            var pending = grid.OnQueryChanged.InvokeAsync(new ClayDataQuery { SearchText = "B" });
            requested.SetResult();
            return pending;
        });
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        a.Release.SetResult();
        await older.WaitAsync(TimeSpan.FromSeconds(5));
        await b.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var interim = menu.Markup;
        b.Release.SetResult();
        await latest.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("initial", interim);
        Assert.DoesNotContain("Не удалось применить", interim);
        Assert.Contains(">B<", menu.Markup);
    }

    /// <summary>Название строки, фильтр, поиск, сортировка и список значений читают общую SQL-проекцию.</summary>
    [Theory]
    [InlineData("en", "Computer", "Computer", false)]
    [InlineData("ru", "Computer", "Компьютер", true)]
    [InlineData(null, "Computer", "Компьютер", true)]
    [InlineData("en", "", "Компьютер", false)]
    [InlineData("en", "\t\u00A0", "Компьютер", false)]
    [InlineData("en", "A'%<b>", "A'%<b>", false)]
    public async Task FilterSortAndDistinctUseTheNameMaterializedByTheSameProjection(
        string? language, string english, string displayedName, bool prefersRussian)
    {
        const string name = "MenuName";
        var rows = new List<Dictionary<string, object?>>
        {
            Script.Row(("Id", 8), ("Name", "Другой тип"), ("NameEn", english), ("MenuName", displayedName)),
        };
        using var connection = MenuConnection(language,
            new ScriptEntry { Rows = rows }, new ScriptEntry { Rows = rows },
            Script.Rows(Script.Row(("MenuValue", null)), Script.Row(("MenuValue", displayedName))));
        await using var context = CreateMenuContext(connection);
        var menu = context.Render<TypeEquipmentMenu>();
        menu.WaitForAssertion(() => Assert.Equal(displayedName, menu.Find("tbody button").TextContent.Trim()));
        Assert.Empty(menu.FindAll("tbody button b"));
        var grid = menu.FindComponent<ClayGrid<EquipmentType>>().Instance;
        var query = new ClayDataQuery
        {
            CompositeFilter = new ClayFilterGroupNode
            {
                Nodes =
                [
                    new ColumnFilter { Column = name, Operator = ColumnFilterOperator.Contains, Value = displayedName },
                    new ColumnFilter { Column = TypeColumn, Operator = ColumnFilterOperator.Equals, Value = 8 },
                ],
            },
            SortColumns = [new(name, true)],
            SearchText = displayedName,
        };

        await menu.InvokeAsync(() => grid.OnQueryChanged.InvokeAsync(query));
        var distinct = await menu.InvokeAsync(() => grid.DistinctValuesLoader!(name, query, 100));

        Assert.Equal(displayedName, menu.Find("tbody button").TextContent.Trim());
        Assert.Equal(displayedName, Assert.Single(distinct.Values));
        Assert.True(distinct.HasBlanks);
        Assert.False(distinct.Capped);
        Assert.Equal(1, distinct.TotalDistinct);
        Assert.All(connection.ParameterLog.Skip(1), parameters => Assert.Equal(prefersRussian, parameters["MenuPrefersRussian"]));
        var filteredSql = connection.CommandLog[2];
        var distinctSql = connection.CommandLog[3];
        Assert.False(grid.Options.ShowPagination);
        Assert.Contains("MenuName LIKE @p0", filteredSql);
        Assert.Contains("MenuName LIKE @search", filteredSql);
        Assert.Equal("%" + displayedName + "%", connection.ParameterLog[2]["search"]);
        Assert.Contains("ORDER BY MenuName DESC", filteredSql);
        Assert.Contains("CASE WHEN @MenuPrefersRussian = 0", filteredSql);
        Assert.Contains("ELSE ТипОборудования", filteredSql);
        var nonBlankPattern = Assert.IsType<string>(connection.ParameterLog[2]["MenuNonBlankPattern"]);
        Assert.Contains('\u00A0', nonBlankPattern);
        Assert.Contains('\t', nonBlankPattern);
        Assert.Equal("%" + displayedName.Replace("%", "\\%") + "%", connection.ParameterLog[2]["p0"]);
        Assert.DoesNotContain(displayedName, filteredSql);
        Assert.Contains("SELECT DISTINCT TOP (@MenuLimit) NULLIF(MenuName", distinctSql);
        Assert.DoesNotContain("MenuName LIKE @p0", distinctSql);
        Assert.Contains("MenuName LIKE @search", distinctSql);
        Assert.Equal(8, connection.ParameterLog[3]["p0"]);
        Assert.Equal(102, connection.ParameterLog[3]["MenuLimit"]);
        var source = filteredSql[filteredSql.IndexOf("FROM (", StringComparison.Ordinal)..filteredSql.IndexOf(") AS MenuTypes", StringComparison.Ordinal)];
        Assert.Contains(source, distinctSql);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static List<Dictionary<string, object?>> MenuRows(string name) =>
        [Script.Row(("Id", 1), ("Name", name), ("NameEn", name), ("MenuName", name))];

    private static ScriptedConnection MenuConnection(string? language, params ScriptEntry[] reads) =>
        new(new Queue<ScriptEntry>([Script.Rows(Script.Row(("Id", 1), ("Language", language))), .. reads]),
            emulateEquipmentIdentity: false);

    private static BunitContext CreateMenuContext(ScriptedConnection connection)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.ComponentFactories.AddStub<InventoryMainMenu>();
        context.Services.AddMudServices();
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddSingleton<Clayzor.Lib.Web.Controls.ClayErrorService>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<Clayzor.Lib.Web.Controls.Services.ClayFormWindowService>();
        context.Services.AddSingleton(new DbManager("Server=test", null, () => connection));
        context.Services.AddSingleton<AuthenticationStateProvider, MenuAuthentication>();
        return context;
    }

    private sealed class MenuAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, @"TEST\menu")], "Negotiate"))));
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

    [Fact]
    public async Task QuickSearchFiltersTheTypeCatalogAndColumnDragIsEnabled()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows([])]));
        using var db = new DbManager("Server=test", null, () => connection);
        var pageType = typeof(Kesco.App.Web.Inventory.Components.Pages.Equipment.TypeEquipmentMenu);
        var page = Activator.CreateInstance(pageType)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        pageType.GetProperty("Db", flags)!.SetValue(page, db);
        var options = (ClayGridOptions)pageType.GetField("_typesOptions", flags)!.GetValue(page)!;

        Assert.True(options.AllowColumnReorder);
        Assert.Equal([EquipmentType.MenuNameColumnName], options.SearchColumns);
        var query = new ClayDataQuery { SearchText = "Компьютер" };
        await (Task)pageType.GetMethod("OnTypesQueryChangedAsync", flags)!.Invoke(page, [query])!;

        Assert.Contains("MenuName LIKE @search", connection.CommandLog[0]);
        Assert.Equal("%Компьютер%", connection.ParameterLog[0]["search"]);
    }
}
