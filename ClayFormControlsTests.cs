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

    /// <summary>Time использует общий компактный класс и отображает секунды полностью.</summary>
    [Fact]
    public async Task Time_UsesSharedCompactClassAndSecondsFormat()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<ClayTime>(parameters => parameters
            .Add(component => component.Value, TimeSpan.Zero));

        Assert.NotNull(cut.Find(".clay-time.mud-picker"));
        Assert.Equal("00:00:00", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>DatePicker использует общий класс фиксированной ширины и полный формат даты.</summary>
    [Fact]
    public async Task DatePicker_UsesSharedFixedWidthClassAndFullDate()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<ClayDatePicker>(parameters => parameters
            .Add(component => component.Value, new DateTime(2026, 11, 22)));

        Assert.NotNull(cut.Find(".clay-date.mud-picker"));
        Assert.Equal("22.11.2026", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>Changed запрашивает смещение на дату записи, учитывая сезонные переходы.</summary>
    [Fact]
    public void Changed_UsesOffsetAtRecordedUtcDate()
    {
        using var ctx = new BunitContext();
        var summer = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var winter = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var summerUtc = summer.ToString("O");
        var winterUtc = winter.ToString("O");
        ctx.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", invocation =>
            invocation.Arguments.Count == 1 && Equals(invocation.Arguments[0], summerUtc)).SetResult(120);
        ctx.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", invocation =>
            invocation.Arguments.Count == 1 && Equals(invocation.Arguments[0], winterUtc)).SetResult(60);
        var cut = ctx.Render<ClayChanged>(parameters => parameters
            .Add(component => component.ChangedById, 3)
            .Add(component => component.ChangedByName, "Иван Иванов")
            .Add(component => component.ChangedAt, summer));

        cut.WaitForAssertion(() => Assert.Contains("15.07.2026 14:00:00", cut.Markup));
        cut.Render(parameters => parameters.Add(component => component.ChangedAt, winter));

        cut.WaitForAssertion(() => Assert.Contains("15.01.2026 13:00:00", cut.Markup));
        var invocations = ctx.JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "clayGridTimeZone.getOffsetMinutes").ToArray();
        Assert.Equal(2, invocations.Length);
        Assert.Equal(summerUtc, invocations[0].Arguments.Single());
        Assert.Equal(winterUtc, invocations[1].Arguments.Single());
    }

    /// <summary>LocalTime применяет смещение браузера на дату события, включая лето и зиму.</summary>
    [Fact]
    public void LocalTime_UsesOffsetAtRecordedUtcDate()
    {
        using var ctx = new BunitContext();
        var summer = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var winter = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        ctx.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", invocation =>
            Equals(invocation.Arguments.Single(), summer.ToString("O"))).SetResult(120);
        ctx.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", invocation =>
            Equals(invocation.Arguments.Single(), winter.ToString("O"))).SetResult(60);
        var cut = ctx.Render<ClayLocalTime>(parameters => parameters.Add(component => component.Value, summer));

        cut.WaitForAssertion(() => Assert.Equal("15.07.2026 14:00:00", cut.Find("time").TextContent));
        cut.Render(parameters => parameters.Add(component => component.Value, winter));

        cut.WaitForAssertion(() => Assert.Equal("15.01.2026 13:00:00", cut.Find("time").TextContent));
    }

    /// <summary>LocalTime трактует SQL datetime с Kind=Unspecified как UTC.</summary>
    [Fact]
    public void LocalTime_UnspecifiedDatabaseValue_UsesClientOffset()
    {
        using var ctx = new BunitContext();
        var databaseValue = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Unspecified);
        var utc = DateTime.SpecifyKind(databaseValue, DateTimeKind.Utc);
        ctx.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", invocation =>
            Equals(invocation.Arguments.Single(), utc.ToString("O"))).SetResult(180);
        var cut = ctx.Render<ClayLocalTime>(parameters => parameters.Add(component => component.Value, databaseValue));

        cut.WaitForAssertion(() => Assert.Equal("15.01.2026 12:00:00", cut.Find("time").TextContent));
    }

    /// <summary>Каркас оставляет toolbar и footer вне прокручиваемого содержимого.</summary>
    [Fact]
    public void EntityForm_KeepsToolbarAndFooterOutsideScrollableContent()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<ClayEntityForm>(parameters => parameters
            .Add(component => component.Toolbar, "<div id='toolbar'>Команды</div>")
            .Add(component => component.ChildContent, "<div id='body'>Поля</div>")
            .Add(component => component.Footer, "<div id='footer'>Изменения</div>"));

        Assert.NotNull(cut.Find(".clay-entity-form > #toolbar"));
        Assert.NotNull(cut.Find(".clay-entity-form-content > #body"));
        Assert.NotNull(cut.Find(".clay-entity-form-layout > .clay-entity-form-footer > #footer"));
        Assert.DoesNotContain("toolbar", cut.Find(".clay-entity-form-content").InnerHtml);
        Assert.DoesNotContain("footer", cut.Find(".clay-entity-form-content").InnerHtml);
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
