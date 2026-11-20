using Bunit;
using Clayzor.Lib.Web.Controls.Components.Navigation;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>bUnit-проверки универсальной многоуровневой меню-компоненты.</summary>
public sealed class ClayNavigationMenuTests
{
    [Fact]
    public async Task NestedItemsAndTwoInstances_AreIndependent()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var firstItems = Items(80);
        var secondItems = Items(2);
        var first = context.Render<ClayNavigationMenu>(parameters => parameters.Add(component => component.Items, firstItems));
        var second = context.Render<ClayNavigationMenu>(parameters => parameters.Add(component => component.Items, secondItems));

        Assert.NotEqual(first.Instance.RootId, second.Instance.RootId);
        first.Find(".clay-navigation-menu__root-button").Click();
        first.WaitForAssertion(() => Assert.Equal(80, first.FindAll(".clay-navigation-menu__link").Count));
        Assert.Empty(second.FindAll(".clay-navigation-menu__panel"));

        first.FindAll(".clay-navigation-menu__link")[1].Click();
        first.WaitForAssertion(() => Assert.Equal(2, first.FindAll(".clay-navigation-menu__panel").Count));
        Assert.Equal(80, first.FindAll("[data-menu-panel='0'] > .clay-navigation-menu__list > .clay-navigation-menu__item").Count);
        Assert.Contains("aria-current", first.Markup);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,test")]
    [InlineData("vbscript:msgbox(1)")]
    public void UnsafeHref_IsRejected(string href)
    {
        Assert.Null(ClayNavigationMenu.SafeHref(href));
    }

    [Fact]
    public void DisabledItem_HasNoHrefAndLongItemsRemainNativeLinks()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var items = new[]
        {
            new ClayNavigationMenuItem
            {
                Text = "Раздел",
                Children =
                [
                    new ClayNavigationMenuItem { Text = new string('Д', 120), Href = "/?id=1", IsActive = true },
                    new ClayNavigationMenuItem { Text = "Недоступно", Disabled = true, Title = "Нет URI" },
                ],
            },
        };
        var cut = context.Render<ClayNavigationMenu>(parameters => parameters.Add(component => component.Items, items));

        cut.Find(".clay-navigation-menu__root-button").Click();

        var links = cut.FindAll(".clay-navigation-menu__link");
        Assert.Equal("/?id=1", links[0].GetAttribute("href"));
        Assert.Equal("page", links[0].GetAttribute("aria-current"));
        Assert.Null(links[1].GetAttribute("href"));
        Assert.Equal("Нет URI", links[1].GetAttribute("title"));
    }

    private static IReadOnlyList<ClayNavigationMenuItem> Items(int count) =>
    [
        new ClayNavigationMenuItem
        {
            Text = "Раздел",
            Children = Enumerable.Range(1, count).Select(index => new ClayNavigationMenuItem
            {
                Text = $"Очень длинный пункт меню {index}",
                Href = $"/?id={index}",
                IsActive = index == 1,
                Children = index == 2
                    ? [new ClayNavigationMenuItem { Text = "Вложенный пункт", Href = "/?id=1001" }]
                    : [],
            }).ToList(),
        },
    ];
}
