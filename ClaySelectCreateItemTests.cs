using Bunit;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверка создания элемента через внешнюю форму ClaySelect.</summary>
public sealed class ClaySelectCreateItemTests
{
    /// <summary>Созданный элемент выбирается и возвращает его идентификатор.</summary>
    [Fact]
    public void CreateItemResult_SelectsReturnedItem()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var item = new TestItem { Id = 9, Name = "Created" };
        int? value = null;
        var cut = ctx.Render<ClaySelect<TestItem, int?>>(parameters => parameters
            .Add(component => component.Items, Array.Empty<TestItem>())
            .Add(component => component.ValueSelector, current => (int?)current.Id)
            .Add(component => component.TextSelector, current => current.Name)
            .Add(component => component.ValueChanged, newValue => value = newValue)
            .Add(component => component.CreateItem, _ => Task.FromResult<ClaySelectExternalResult<TestItem, int?>?>(
                ClaySelectExternalResult<TestItem, int?>.FromItem(item))));

        cut.Find(".clay-select-button").Click();
        cut.Find(".clay-select-create").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(9, value);
            Assert.Equal("Created", cut.Find("input").GetAttribute("value"));
        });
    }

    private sealed class TestItem
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}
