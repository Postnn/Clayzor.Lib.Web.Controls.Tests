using Bunit;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки обязательности общих полей в разных режимах редактирования.</summary>
public sealed class ClayRequiredFieldStyleTests
{
    /// <summary>Жёлтая заливка списка применима только к пустому редактируемому обязательному полю.</summary>
    [Theory]
    [InlineData(null, true, false, false, true)]
    [InlineData(1, true, false, false, false)]
    [InlineData(null, false, false, false, false)]
    [InlineData(null, true, true, false, false)]
    [InlineData(null, true, false, true, false)]
    public async Task DropdownHighlightsOnlyEmptyEditableRequiredField(
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

    /// <summary>Справочник показывает заливку до выбора элемента.</summary>
    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, true, false)]
    [InlineData(0, false, false)]
    public async Task ComboBoxHighlightsOnlyEmptyRequiredField(int value, bool required, bool highlight)
    {
        await using var context = CreateContext();
        var field = context.Render<ClayComboBox<Option>>(parameters => parameters
            .Add(component => component.Items, [new Option(1, "First")])
            .Add(component => component.Value, value)
            .Add(component => component.Required, required));

        Assert.Equal(highlight, field.FindAll(".clay-required-empty .mud-input-outlined").Count == 1);
    }

    /// <summary>Текст, placeholder и семантическая обязательность обновляются независимо.</summary>
    [Fact]
    public async Task TextBoxSuppliesEmptyInputAndPlaceholderToLiveCss()
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

        field.Render(parameters => parameters.Add(component => component.Value, ""));
        Assert.Equal("", field.Find(".clay-required-editable input[required]").GetAttribute("value"));

        field.Render(parameters => parameters
            .Add(component => component.Options, new ClayTextBoxOptions
            {
                Required = true,
                ReadOnly = true,
            }));
        var readOnly = field.Find("input[readonly]");
        Assert.Equal("", readOnly.GetAttribute("value"));
        Assert.Empty(field.FindAll(".clay-required-editable"));
    }

    /// <summary>Интерактивный календарь допускает выбор даже при нередактируемом вручную вводе.</summary>
    [Fact]
    public async Task DatePickerSupportsEmptyHighlightWithTechnicalReadOnlyInput()
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
        Assert.Equal(" ", input.GetAttribute("placeholder"));
        Assert.True(input.HasAttribute("data-clay-focus-editor"));
        Assert.True(input.HasAttribute("data-clay-enter-native"));

        field.Render(parameters => parameters.Add(component => component.Value, (DateTime?)null));
        var emptyInput = Assert.IsAssignableFrom<AngleSharp.Html.Dom.IHtmlInputElement>(
            field.Find(".clay-required-editable input[readonly]"));
        Assert.Empty(emptyInput.Value);

        field.Render(parameters => parameters
            .Add(component => component.Options, new ClayDatePickerOptions
            {
                Required = true,
                Disabled = true,
            }));
        Assert.False(field.Find("input[disabled]").HasAttribute("data-clay-focus-editor"));
        Assert.Empty(field.FindAll(".clay-required-editable"));
    }

    [Fact]
    public async Task ClockRemainsATabTargetWithNativeKeyboardWhenPopupOnly()
    {
        await using var context = CreateContext();
        var field = context.Render<ClayTime>(p =>
            p.Add(x => x.Options, new ClayTimeOptions { Editable = false }));
        var input = field.Find("input[readonly]");
        Assert.True(input.HasAttribute("data-clay-focus-editor"));
        Assert.True(input.HasAttribute("data-clay-enter-native"));
        field.Render(p => p.Add(x => x.Options, new ClayTimeOptions { ReadOnly = true }));
        Assert.False(field.Find("input[readonly]").HasAttribute("data-clay-focus-editor"));
    }

    /// <summary>Поисковая строка не заменяет значение, выбранное в обязательном селекторе.</summary>
    [Fact]
    public async Task SelectHighlightsMissingSelectionUntilChoiceAndAgainAfterClear()
    {
        await using var context = CreateContext();
        var field = context.Render<ClaySelect<Option, int?>>(parameters => parameters
            .Add(component => component.Items, [new Option(1, "First")])
            .Add(component => component.ValueSelector, option => (int?)option.Id)
            .Add(component => component.TextSelector, option => option.Name)
            .Add(component => component.Options, new ClaySelectOptions { Required = true, Placeholder = "Выберите" }));

        Assert.NotNull(field.Find("input[required][data-clay-required-empty]"));
        await field.InvokeAsync(() => field.Instance.RestoreDraftTextAsync("непринятый поиск"));
        Assert.Equal("непринятый поиск", field.Find("input[data-clay-required-empty]").GetAttribute("value"));

        field.Render(parameters => parameters.Add(component => component.Value, 1));
        Assert.Equal("First", field.Find("input[required]").GetAttribute("value"));
        Assert.Empty(field.FindAll("[data-clay-required-empty]"));

        field.Render(parameters => parameters.Add(component => component.Value, (int?)null));
        Assert.NotNull(field.Find("input[required][data-clay-required-empty]"));
        field.Render(parameters => parameters
            .Add(component => component.Options, new ClaySelectOptions { Required = true, ReadOnly = true }));
        Assert.NotNull(field.Find("input[readonly]"));
        Assert.Empty(field.FindAll("[data-clay-required-empty]"));
        field.Render(parameters => parameters
            .Add(component => component.Options, new ClaySelectOptions { Required = true, Disabled = true }));
        Assert.NotNull(field.Find("input[disabled]"));
        Assert.Empty(field.FindAll("[data-clay-required-empty]"));
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
