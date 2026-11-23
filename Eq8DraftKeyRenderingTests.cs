using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки стабильных DR-01 keys и восстановления текста ClaySelect без выбора.</summary>
public sealed class Eq8DraftKeyRenderingTests
{
    /// <summary>Общие wrappers выводят явный draft key в реальный input.</summary>
    [Fact]
    public async Task Inputs_RenderStableDraftKeys()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var text = context.Render<ClayTextBox>(parameters => parameters
            .Add(component => component.Options, new ClayTextBoxOptions { DraftKey = "test.text" }));
        var select = context.Render<ClaySelect<Item, int?>>(parameters => parameters
            .Add(component => component.Options, new ClaySelectOptions { DraftKey = "test.select" })
            .Add(component => component.Items, [new Item(1, "One")])
            .Add(component => component.ValueSelector, item => (int?)item.Id)
            .Add(component => component.TextSelector, item => item.Name));

        Assert.Equal("test.text", text.Find("input").GetAttribute("data-draft-key"));
        Assert.Equal("test.select", select.Find("input").GetAttribute("data-draft-key"));
    }

    /// <summary>RestoreDraftText меняет только текст, не ID, не SelectedItemChanged и не поисковый запрос.</summary>
    [Fact]
    public async Task SelectRestore_PreservesIdAndSkipsBusinessEvents()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var selectedNotifications = 0;
        var select = context.Render<ClaySelect<Item, int?>>(parameters => parameters
            .Add(component => component.Value, 1)
            .Add(component => component.Items, [new Item(1, "One")])
            .Add(component => component.ValueSelector, item => (int?)item.Id)
            .Add(component => component.TextSelector, item => item.Name)
            .Add(component => component.SelectedItemChanged, _ => selectedNotifications++));

        var initialNotifications = selectedNotifications;
        await select.InvokeAsync(() => select.Instance.RestoreDraftTextAsync("unfinished lookup"));

        Assert.Equal(1, select.Instance.Value);
        Assert.Equal("unfinished lookup", select.Find("input").GetAttribute("value"));
        Assert.Equal(initialNotifications, selectedNotifications);
    }

    private sealed record Item(int Id, string Name);
}
