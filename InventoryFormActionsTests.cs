using System.Reflection;
using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Themes;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components.Layout;
using Kesco.App.Web.Inventory.Components.Pages.Telefon;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class InventoryFormActionsTests
{
    [Fact]
    public void EveryProductionRouteHasRegisteredDocumentation()
    {
        var routes = typeof(MainLayout).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template)
            .Where(route => route is not ("/menu-test" or "/select-test" or "/tree-test"))
            .ToArray();

        Assert.NotEmpty(routes);
        foreach (var route in routes)
            Assert.NotNull(InventoryFormHelp.Find(route.TrimStart('/')));
    }

    [Theory]
    [InlineData("/Equipment/EquipmentForm", "Inventory/Equipment/Equipment_form.aspx")]
    [InlineData("/Reports/Modifications.aspx", "Inventory/Reports/Modifications.aspx")]
    [InlineData("/Default/FinderTel", "Inventory/Default/FinderTel.aspx")]
    [InlineData("/", "Inventory/default.aspx")]
    public void PathBaseAndUserDataDoNotChangeTheDocumentation(string route, string documentationPath)
    {
        var navigation = new TestNavigationManager(route);
        var form = InventoryFormHelp.Find(navigation.ToBaseRelativePath(navigation.Uri));

        Assert.Equal(documentationPath, form?.DocumentationPath);
    }

    [Fact]
    public async Task ActiveParallelCallModalHasItsOwnHelpAndThemeActions()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddScoped<ClayThemeState>();
        context.Services.AddSingleton(new ClayAppSettings { UriWikiHelp = "https://docs.example/wiki" });
        var parentActions = context.Render<ClayFormActions>(parameters =>
            parameters.Add(component => component.Form, InventoryFormHelp.Tel));
        context.Render<MudPopoverProvider>();
        var provider = context.Render<MudDialogProvider>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();

        await provider.InvokeAsync(() => dialogs.ShowAsync<PhoneParallelCallDialog>("Параллельный вызов"));
        var modal = provider.WaitForElement(".mud-dialog");
        var link = Assert.Single(modal.QuerySelectorAll("a[aria-label='Документация']"));
        Assert.Equal("https://docs.example/wiki/Inventory/Telefon/Tel.aspx", link.GetAttribute("href"));
        Assert.Single(modal.QuerySelectorAll("[aria-label='Тема оформления']"));
        Assert.Single(parentActions.FindAll("[aria-label='Тема оформления']"));
        Assert.Empty(provider.FindComponents<ClayThemeHost>());

        var editor = provider.FindComponent<PhoneParallelCallDialog>().Instance;
        editor.Value.CallNumber = "123";
        await provider.InvokeAsync(() =>
            context.Services.GetRequiredService<ClayThemeState>().SelectAsync(ClayThemePreference.ClassicDark));
        Assert.Same(editor, provider.FindComponent<PhoneParallelCallDialog>().Instance);
        Assert.Equal("123", editor.Value.CallNumber);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string route)
        {
            const string origin = "https://inventory.example/proxy/v2/";
            Initialize(origin, origin + route.TrimStart('/') + "?id=76&token=not-for-wiki#draft");
        }
    }
}
