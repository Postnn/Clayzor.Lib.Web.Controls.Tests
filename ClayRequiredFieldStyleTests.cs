using Bunit;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки обязательности общих полей в разных режимах редактирования.</summary>
public sealed class ClayRequiredFieldStyleTests
{
    /// <summary>Список сохраняет обязательность после выбора и снимает её в режиме просмотра.</summary>
    [Theory]
    [InlineData(null, true, false, false, true)]
    [InlineData(1, true, false, false, true)]
    [InlineData(null, false, false, false, false)]
    [InlineData(null, true, true, false, false)]
    [InlineData(null, true, false, true, false)]
    public async Task DropdownHighlightsEditableRequiredFieldRegardlessOfValue(
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

        Assert.Equal(highlight, field.FindAll(".clay-required-editable .mud-input-outlined").Count == 1);
    }

    /// <summary>Справочник сохраняет обязательность с выбранным элементом.</summary>
    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(0, false, false)]
    public async Task ComboBoxHighlightsRequiredFieldRegardlessOfDisplayedItem(int value, bool required, bool highlight)
    {
        await using var context = CreateContext();
        var field = context.Render<ClayComboBox<Option>>(parameters => parameters
            .Add(component => component.Items, [new Option(1, "First")])
            .Add(component => component.Value, value)
            .Add(component => component.Required, required));

        Assert.Equal(highlight, field.FindAll(".clay-required-editable .mud-input-outlined").Count == 1);
    }

    /// <summary>Текст, placeholder и семантическая обязательность обновляются независимо.</summary>
    [Fact]
    public async Task TextBoxPreservesRequiredForFilledTextAndClearsHighlightWhenReadOnly()
    {
        await using var context = CreateContext();
        var field = context.Render<ClayTextBox>(parameters => parameters
            .Add(component => component.Value, "Готовое значение")
            .Add(component => component.Options, new ClayTextBoxOptions
            {
                Required = true,
                Placeholder = "Подсказка",
            }));

        var input = field.Find(".clay-required-editable input[required]");
        Assert.Equal("Готовое значение", input.GetAttribute("value"));
        Assert.Equal("Подсказка", input.GetAttribute("placeholder"));

        field.Render(parameters => parameters
            .Add(component => component.Options, new ClayTextBoxOptions
            {
                Required = true,
                ReadOnly = true,
            }));
        var readOnly = field.Find("input[readonly]");
        Assert.Equal("Готовое значение", readOnly.GetAttribute("value"));
        Assert.Empty(field.FindAll(".clay-required-editable"));
    }

    /// <summary>Интерактивный календарь допускает выбор даже при нередактируемом вручную вводе.</summary>
    [Fact]
    public async Task DatePickerHighlightsCalendarWithReadOnlyTextInput()
    {
        await using var context = CreateContext();
        var date = new DateTime(2026, 11, 26);
        var field = context.Render<ClayDatePicker>(parameters => parameters
            .Add(component => component.Value, date)
            .Add(component => component.Options, new ClayDatePickerOptions
            {
                Required = true,
                Editable = false,
                DateFormat = "yyyy-MM-dd",
            }));

        var input = field.Find(".clay-required-editable .mud-input-control .mud-input input[readonly]");
        Assert.Equal("2026-11-26", input.GetAttribute("value"));

        field.Render(parameters => parameters
            .Add(component => component.Options, new ClayDatePickerOptions
            {
                Required = true,
                Disabled = true,
            }));
        Assert.NotNull(field.Find("input[disabled]"));
        Assert.Empty(field.FindAll(".clay-required-editable"));
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
