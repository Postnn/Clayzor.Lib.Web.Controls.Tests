using System.Globalization;
using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет числовую конверсию и принятие значения до перевода фокуса.</summary>
public sealed class ClayNumericEntryFocusTests
{
    [Theory]
    [InlineData("en-US", "4.50", true, true)]
    [InlineData("ru-RU", "4,50", true, true)]
    [InlineData("en-US", "4.50", false, false)]
    [InlineData("en-US", "abc", true, false)]
    [InlineData("en-US", "11", true, false)]
    [InlineData("en-US", "", true, false)]
    public async Task EnterUsesNativeDecimalCultureAndWaitsForAcceptedValue(string culture, string raw,
        bool accept, bool advance)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var context = new BunitContext();
            context.Services.AddMudServices();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.JSInterop.SetupModule("./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js")
                .Setup<ClayTextEntry?>("claimTextEntry", _ => true).SetResult(new ClayTextEntry(29, raw));
            var callbacks = new List<decimal?>();
            IRenderedComponent<ClayNumber>? field = null;
            field = context.Render<ClayNumber>(p => p
                .Add(x => x.Options, new ClayNumberOptions { Min = 1, Max = 10, Precision = 2, Required = true })
                .Add(x => x.Value, 3m)
                .Add(x => x.ValueChanged, EventCallback.Factory.Create<decimal?>(new object(), value =>
                {
                    callbacks.Add(value);
                    if (accept) field!.Render(parameters => parameters.Add(x => x.Value, value));
                })));
            var input = field.FindComponent<ClayNumericInput>();
            Assert.NotNull(field.Find("input").GetAttribute("data-clay-text-commit"));
            await field.InvokeAsync(() => input.Instance.OnKeyDown.InvokeAsync(new KeyboardEventArgs { Key = "Enter" }));

            var valid = raw is "4.50" or "4,50";
            Assert.Equal(advance ? 1 : 0, context.JSInterop.Invocations["completeTextEntry"].Count);
            Assert.Equal(valid && accept ? 4.50m : 3m, field.Instance.Value);
            if (valid) Assert.Equal(4.50m, Assert.Single(callbacks));
            else
            {
                Assert.Empty(callbacks);
                Assert.True(input.Instance.HasErrors);
            }
        }
        finally { CultureInfo.CurrentUICulture = previousCulture; }
    }
}
