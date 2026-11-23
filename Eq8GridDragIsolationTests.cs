using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки изоляции drag-session и DOM-идентификаторов статических гридов EQ8.</summary>
public sealed class Eq8GridDragIsolationTests
{
    /// <summary>Чужой drag не заменяет колонку активного перетаскивания конкретного грида.</summary>
    [Fact]
    public async Task InterleavedGrids_DropUsesOnlyOwnerDraggedColumn()
    {
        await using var ctx = CreateContext();
        var first = RenderGrid(ctx, "A");
        var second = RenderGrid(ctx, "B");
        first.Find(".grouping-toggle-btn").Click();
        second.Find(".grouping-toggle-btn").Click();

        first.Instance.SetDraggedColumn("A");
        second.Instance.SetDraggedColumn("B");
        first.Find(".grouping-tray").Drop(new DragEventArgs());

        Assert.Equal(["A"], first.Instance.GroupColumns);
        Assert.Empty(second.Instance.GroupColumns);

        second.Find(".grouping-tray").Drop(new DragEventArgs());
        Assert.Equal(["B"], second.Instance.GroupColumns);
    }

    /// <summary>Независимые renderer-контексты моделируют два circuit в одном процессе.</summary>
    [Fact]
    public async Task IndependentCircuits_DoNotShareDraggedColumn()
    {
        await using var firstCircuit = CreateContext();
        await using var secondCircuit = CreateContext();
        var first = RenderGrid(firstCircuit, "A");
        var second = RenderGrid(secondCircuit, "B");
        first.Find(".grouping-toggle-btn").Click();
        second.Find(".grouping-toggle-btn").Click();

        first.Instance.SetDraggedColumn("A");
        second.Instance.SetDraggedColumn("B");
        first.Find(".grouping-tray").Drop(new DragEventArgs());

        Assert.Equal(["A"], first.Instance.GroupColumns);
        Assert.Empty(second.Instance.GroupColumns);
    }

    /// <summary>Отмена drag одного грида не очищает активную сессию другого.</summary>
    [Fact]
    public async Task CancelOwnerDrag_PreservesOtherGridSession()
    {
        await using var ctx = CreateContext();
        var first = RenderGrid(ctx, "A");
        var second = RenderGrid(ctx, "B");
        second.Find(".grouping-toggle-btn").Click();

        first.Instance.SetDraggedColumn("A");
        second.Instance.SetDraggedColumn("B");
        first.Instance.SetDraggedColumn(null);
        second.Find(".grouping-tray").Drop(new DragEventArgs());

        Assert.Equal(["B"], second.Instance.GroupColumns);
    }

    /// <summary>Освобождение владельца очищает только его drag-session.</summary>
    [Fact]
    public async Task DisposeOwnerDrag_DoesNotLeakColumnToAnotherGrid()
    {
        await using var ctx = CreateContext();
        var first = RenderGrid(ctx, "A");
        var second = RenderGrid(ctx, "A");
        second.Find(".grouping-toggle-btn").Click();

        first.Instance.SetDraggedColumn("A");
        await first.Instance.DisposeAsync();
        second.Find(".grouping-tray").Drop(new DragEventArgs());

        Assert.Empty(second.Instance.GroupColumns);
    }

    /// <summary>Дефолтные статические гриды получают разные стабильные DOM ID без смены публичного default.</summary>
    [Fact]
    public async Task DefaultStaticGrids_HaveUniqueStableDomIds()
    {
        await using var ctx = CreateContext();
        var first = RenderGrid(ctx, "A");
        var second = RenderGrid(ctx, "B");

        var firstId = first.Find(".mud-paper").Id;
        var secondId = second.Find(".mud-paper").Id;
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("clay-grid", new ClayGridOptions().Id);

        first.Render();
        Assert.Equal(firstId, first.Find(".mud-paper").Id);
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

    private static IRenderedComponent<ClayGrid<TestRow>> RenderGrid(BunitContext ctx, string column) =>
        ctx.Render<ClayGrid<TestRow>>(parameters => parameters
            .Add(component => component.Items, [new TestRow()])
            .Add(component => component.Options, new ClayGridOptions
            {
                ShowAddButton = false,
                ShowPagination = false,
                AutoHeight = true,
            })
            .Add(component => component.ColumnDefs, ColumnDefs(column))
            .Add(component => component.Columns, Columns()));

    private static RenderFragment ColumnDefs(string column) => builder =>
    {
        builder.OpenComponent<ClayColumnDef>(0);
        builder.AddAttribute(1, nameof(ClayColumnDef.ColumnId), 1);
        builder.AddAttribute(2, nameof(ClayColumnDef.SqlName), column);
        builder.AddAttribute(3, nameof(ClayColumnDef.DisplayName), column);
        builder.AddAttribute(4, nameof(ClayColumnDef.Groupable), true);
        builder.CloseComponent();
    };

    private static RenderFragment Columns() => builder =>
    {
        builder.OpenComponent<ClayColumn<TestRow>>(0);
        builder.AddAttribute(1, nameof(ClayColumn<TestRow>.ColumnId), 1);
        builder.AddAttribute(2, nameof(ClayColumn<TestRow>.TextSelector), (Func<TestRow, string?>)(_ => "Значение"));
        builder.CloseComponent();
    };

    private sealed class TestRow;
}
