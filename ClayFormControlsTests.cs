using Bunit;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки общих контроллов форм Clayzor.</summary>
public sealed class ClayFormControlsTests
{
    /// <summary>TextBox передаёт значение и применяет MaxLength.</summary>
    [Fact]
    public void TextBox_BindsValueAndMaxLength()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        string? value = null;
        var cut = ctx.Render<ClayTextBox>(parameters => parameters
            .Add(component => component.ValueChanged, newValue => value = newValue)
            .Add(component => component.Options, new ClayTextBoxOptions { MaxLength = 100, Required = true }));

        cut.Find("input").Input("Model");

        Assert.Equal("Model", value);
        Assert.Equal("100", cut.Find("input").GetAttribute("maxlength"));
        Assert.Contains("mud-input-outlined", cut.Markup);
    }

    /// <summary>Changed показывает сотрудника и дату существующей записи.</summary>
    [Fact]
    public void Changed_ShowsEmployeeAndDate()
    {
        using var ctx = new BunitContext();
        var changedAt = new DateTime(2026, 11, 17, 12, 30, 0);
        var cut = ctx.Render<ClayChanged>(parameters => parameters
            .Add(component => component.ChangedById, 3)
            .Add(component => component.ChangedByName, "Иван Иванов")
            .Add(component => component.ChangedAt, changedAt));

        Assert.Contains("Иван Иванов", cut.Markup);
        Assert.Contains("17.11.2026 12:30:00", cut.Markup);
    }

    /// <summary>Checkbox изменяется с клавиатуры и не изменяется в ReadOnly.</summary>
    [Fact]
    public void Checkbox_SupportsKeyboardAndReadOnly()
    {
        using var ctx = new BunitContext();
        bool? value = false;
        var cut = ctx.Render<ClayCheckbox>(parameters => parameters
            .Add(component => component.State, value)
            .Add(component => component.StateChanged, newValue => value = newValue));
        cut.Find("[role=checkbox]").KeyDown(new KeyboardEventArgs { Key = " " });
        Assert.True(value);

        cut.Render(parameters => parameters.Add(component => component.ReadOnly, true));
        cut.Find("[role=checkbox]").Click();
        Assert.True(value);
    }
}
