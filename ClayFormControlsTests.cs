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

    /// <summary>Нулевой MaxLength означает отсутствие прикладного ограничения и разрешает ручной ввод.</summary>
    [Fact]
    public void TextArea_ZeroMaxLength_DoesNotProduceZeroHtmlLimit()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        string? value = null;
        var cut = ctx.Render<ClayTextArea>(parameters => parameters
            .Add(component => component.ValueChanged, newValue => value = newValue)
            .Add(component => component.Options, new ClayTextAreaOptions { MaxLength = 0 }));

        cut.Find("textarea").Input("template");

        Assert.Equal("template", value);
        Assert.Equal(int.MaxValue.ToString(), cut.Find("textarea").GetAttribute("maxlength"));
    }

    /// <summary>Changed показывает сотрудника и переводит UTC-время в часовой пояс клиента.</summary>
    [Fact]
    public void Changed_ShowsEmployeeAndConvertsUtcToClientTime()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", _ => true).SetResult(180);
        var changedAt = new DateTime(2026, 11, 17, 12, 30, 0, DateTimeKind.Utc);
        var cut = ctx.Render<ClayChanged>(parameters => parameters
            .Add(component => component.ChangedById, 3)
            .Add(component => component.ChangedByName, "Иван Иванов")
            .Add(component => component.ChangedAt, changedAt));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Иван Иванов", cut.Markup);
            Assert.Contains("17.11.2026 15:30:00", cut.Markup);
        });
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
