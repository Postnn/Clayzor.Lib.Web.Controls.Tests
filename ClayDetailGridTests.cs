using Bunit;
using Clayzor.Lib.Web.Controls.Components.Detail;
using Microsoft.AspNetCore.Components;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки общей компактной табличной секции.</summary>
public sealed class ClayDetailGridTests
{
    /// <summary>Таблица скрывает колонку, выводит шаблон ячейки и применяет класс строки.</summary>
    [Fact]
    public void Grid_SupportsConditionalColumnsCellTemplatesAndRowClasses()
    {
        using var ctx = new BunitContext();
        IReadOnlyList<ClayDetailColumn<string>> columns =
        [
            new()
            {
                Title = "Номер",
                TextSelector = item => item,
                CellTemplate = item => builder => builder.AddMarkupContent(0, $"<a href=\"/phone?id={item}\">{item}</a>")
            },
            new() { Title = "Скрытая", TextSelector = item => item, IsVisible = false }
        ];
        var cut = ctx.Render<ClayDetailGrid<string>>(parameters => parameters
            .Add(component => component.Items, ["101", "102"])
            .Add(component => component.Columns, columns)
            .Add(component => component.RowClass, item => item == "102" ? "overdue" : null));

        Assert.DoesNotContain("Скрытая", cut.Markup);
        Assert.Equal(2, cut.FindAll("a[href^='/phone?id=']").Count);
        Assert.Single(cut.FindAll("tr.overdue"));
    }
}
