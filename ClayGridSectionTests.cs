using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки статического ClayGrid в режиме табличной секции.</summary>
public sealed class ClayGridSectionTests
{
    /// <summary>Секция скрывает toolbar и выводит текст, класс строки и добавление в сервисной шапке.</summary>
    [Fact]
    public async Task SectionGrid_UsesTextSelectorRowClassAndServiceHeaderAdd()
    {
        await using var ctx = CreateContext();
        var added = 0;
        var cut = ctx.Render<ClayGrid<string>>(parameters => parameters
            .Add(component => component.Items, ["101", "102"])
            .Add(component => component.Options, Options())
            .Add(component => component.ColumnDefs, ColumnDefs())
            .Add(component => component.Columns, Columns())
            .Add(component => component.RowClass, item => item == "102" ? "overdue" : null)
            .Add(component => component.OnAdd, () => added++));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));
        Assert.Equal("101", cut.Find("tbody tr").TextContent.Trim());
        Assert.Single(cut.FindAll("tr.overdue"));
        Assert.DoesNotContain("Секция", cut.Markup);
        Assert.DoesNotContain("clay-grid-search", cut.Markup);
        Assert.DoesNotContain("clay-grid-paginator", cut.Markup);
        Assert.Empty(cut.FindAll(".filter-tray"));
        Assert.Contains("width:88px", cut.Find("thead th").GetAttribute("style"));
        Assert.NotNull(cut.Find(".clay-grid-service-add"));
        cut.Find("thead th button").Click();
        Assert.Equal(1, added);
    }

    /// <summary>Пустая секция выводит заданный текст и сохраняет команду добавления.</summary>
    [Fact]
    public async Task SectionGrid_ShowsEmptyTextAndAddCommand()
    {
        await using var ctx = CreateContext();
        var cut = ctx.Render<ClayGrid<string>>(parameters => parameters
            .Add(component => component.Items, [])
            .Add(component => component.Options, Options())
            .Add(component => component.ColumnDefs, ColumnDefs())
            .Add(component => component.Columns, Columns())
            .Add(component => component.OnAdd, () => { }));

        cut.WaitForAssertion(() => Assert.Contains("Нет номеров", cut.Markup));
        Assert.NotNull(cut.Find("thead th button"));
    }

    /// <summary>Панель фильтрации учитывает начальную настройку и сохраняет ручное переключение.</summary>
    [Fact]
    public async Task FilterTray_UsesConfiguredInitialStateAndToggle()
    {
        await using var ctx = CreateContext();
        var options = Options();
        options.ShowToolbar = true;
        options.FilterTrayExpandedByDefault = true;
        var cut = ctx.Render<ClayGrid<string>>(parameters => parameters
            .Add(component => component.Items, ["101"])
            .Add(component => component.Options, options)
            .Add(component => component.ColumnDefs, ColumnDefs())
            .Add(component => component.Columns, Columns()));

        Assert.Single(cut.FindAll(".filter-tray"));
        cut.Find(".filter-toggle-btn").Click();
        Assert.Empty(cut.FindAll(".filter-tray"));
        cut.Find(".filter-toggle-btn").Click();
        Assert.Single(cut.FindAll(".filter-tray"));
    }

    /// <summary>Container-height скрывает фильтр без резервирования места и не меняет высоту грида.</summary>
    [Fact]
    public async Task FillHeight_FilterToggleDoesNotChangeGridHeight()
    {
        await using var ctx = CreateContext();
        var options = Options();
        options.ShowToolbar = true;
        options.FilterTrayExpandedByDefault = true;
        options.FillAvailableHeight = true;
        var cut = ctx.Render<ClayGrid<string>>(parameters => parameters
            .Add(component => component.Items, ["101"])
            .Add(component => component.Options, options)
            .Add(component => component.ColumnDefs, ColumnDefs())
            .Add(component => component.Columns, Columns()));
        var gridStyle = cut.Find(".mud-data-grid").GetAttribute("style");

        cut.Find(".filter-toggle-btn").Click();
        Assert.Empty(cut.FindAll(".filter-tray"));
        Assert.Equal(gridStyle, cut.Find(".mud-data-grid").GetAttribute("style"));
        cut.Find(".filter-toggle-btn").Click();
        Assert.Single(cut.FindAll(".filter-tray"));
        Assert.Equal(gridStyle, cut.Find(".mud-data-grid").GetAttribute("style"));
    }

    /// <summary>Container-height включает отдельный класс, а hidetitle скрывает только текст заголовка.</summary>
    [Fact]
    public async Task SectionGrid_FillHeightAndHiddenTitlePreserveToolbar()
    {
        await using var ctx = CreateContext();
        var options = Options();
        options.ShowToolbar = true;
        options.ShowTitle = false;
        options.FillAvailableHeight = true;
        options.CompactLayout = true;
        var cut = ctx.Render<ClayGrid<string>>(parameters => parameters
            .Add(component => component.Items, ["101"])
            .Add(component => component.Options, options)
            .Add(component => component.ColumnDefs, ColumnDefs())
            .Add(component => component.Columns, Columns()));

        Assert.Contains("clay-grid--fill-height", cut.Markup);
        Assert.Contains("clay-grid--compact-layout", cut.Markup);
        Assert.DoesNotContain("<h5", cut.Markup);
        Assert.NotNull(cut.Find(".toolbar-columns-btn"));
        Assert.NotNull(cut.Find(".toolbar-add-btn"));
        Assert.Empty(cut.FindAll(".clay-grid-service-add"));
    }

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(new ClayAppSettings());
        ctx.Services.AddScoped<ClayErrorService>();
        ctx.Services.AddScoped<DbManager>(_ => new DbManager("Server=test"));
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ctx.Services.AddSingleton(new ClayFormWindowService(ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton<NavigationManager>(new FakeNavigationManager());
        return ctx;
    }

    private static ClayGridOptions Options() => new()
    {
        Title = "Секция",
        Id = "section-grid",
        EmptyText = "Нет номеров",
        ShowToolbar = false,
        ShowPagination = false,
        AutoHeight = true,
        AllowColumnReorder = false,
        AllowSorting = false,
        ColumnMenuMode = ColumnMenuMode.Hidden,
    };

    private static RenderFragment ColumnDefs() => builder =>
    {
        builder.OpenComponent<ClayColumnDef>(0);
        builder.AddAttribute(1, nameof(ClayColumnDef.ColumnId), 1);
        builder.AddAttribute(2, nameof(ClayColumnDef.SqlName), "Number");
        builder.AddAttribute(3, nameof(ClayColumnDef.DisplayName), "Номер");
        builder.AddAttribute(4, nameof(ClayColumnDef.Filterable), true);
        builder.CloseComponent();
    };

    private static RenderFragment Columns() => builder =>
    {
        builder.OpenComponent<ClayColumn<string>>(0);
        builder.AddAttribute(1, nameof(ClayColumn<string>.ColumnId), 1);
        builder.AddAttribute(2, nameof(ClayColumn<string>.TextSelector), (Func<string, string?>)(item => item));
        builder.CloseComponent();
    };
}
