using Bunit;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class ClayRequiredFieldStyleTests
{
    [Theory]
    [InlineData(null, true, false, false, true)]
    [InlineData(1, true, false, false, false)]
    [InlineData(null, false, false, false, false)]
    [InlineData(null, true, true, false, false)]
    [InlineData(null, true, false, true, false)]
    public async Task DropdownHighlightsOnlyAnEditableRequiredFieldWithNoValue(
        int? value, bool required, bool disabled, bool readOnly, bool highlight)
    {
        await using var context = CreateContext();
        var field = context.Render<ClayDropDownList<Option, int?>>(parameters => parameters
            .Add(component => component.Items, [new Option(1, "First")])
            .Add(component => component.Value, value)
            .Add(component => component.ValueSelector, option => option.Id)
            .Add(component => component.TextSelector, option => option.Name)
            .Add(component => component.Options, new ClayDropDownListOptions
            {
                Required = required,
                Disabled = disabled,
                ReadOnly = readOnly,
            }));

        Assert.Equal(highlight, field.FindAll(".clay-required-empty .mud-input-outlined").Count == 1);
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, true, false)]
    [InlineData(0, false, false)]
    public async Task ComboBoxHighlightsOnlyARequiredFieldWithNoDisplayedItem(int value, bool required, bool highlight)
    {
        await using var context = CreateContext();
        var field = context.Render<ClayComboBox<Option>>(parameters => parameters
            .Add(component => component.Items, [new Option(1, "First")])
            .Add(component => component.Value, value)
            .Add(component => component.Required, required));

        Assert.Equal(highlight, field.FindAll(".clay-required-empty .mud-input-outlined").Count == 1);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Render<MudPopoverProvider>();
        return context;
    }

    private sealed record Option(int Id, string Name) : ILookupEntity;
}
