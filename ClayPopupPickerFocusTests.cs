using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет, что календарь и часы завершают переход только после принятого выбора и закрытия.</summary>
public sealed class ClayPopupPickerFocusTests
{
    [Theory]
    [InlineData("confirmed", true)]
    [InlineData("outside", false)]
    [InlineData("rejected", false)]
    [InlineData("reopened", false)]
    [InlineData("escape", false)]
    [InlineData("readonly", false)]
    public async Task DateRequiresCurrentConfirmedAndAcceptedSelection(string mode, bool advances)
    {
        await using var context = Setup(mode == "outside" ? null : 18L);
        var current = new DateTime(2026, 11, 27);
        var field = context.Render<ClayDatePicker>(p => p
            .Add(x => x.Value, current)
            .Add(x => x.Options, new ClayDatePickerOptions { ReadOnly = mode == "readonly" }));
        var picker = field.FindComponent<ClayConfirmedDateInput>();
        await field.InvokeAsync(async () =>
        {
            await picker.Instance.PickerOpened.InvokeAsync();
            if (mode != "escape")
                await picker.Instance.Submitted.InvokeAsync(mode == "rejected" ? current.AddDays(1) : current);
            if (mode == "reopened") await picker.Instance.PickerOpened.InvokeAsync();
        });
        Assert.Empty(context.JSInterop.Invocations["completePopupSelection"]);
        await field.InvokeAsync(() => picker.Instance.PickerClosed.InvokeAsync());
        await field.InvokeAsync(() => picker.Instance.PickerClosed.InvokeAsync());
        Assert.Equal(advances ? 1 : 0, context.JSInterop.Invocations["completePopupSelection"].Count);
    }

    [Fact]
    public async Task TimeRepeatedNativeSubmitCompletesOneTransition()
    {
        await using var context = Setup(21);
        var time = new TimeSpan(14, 20, 0);
        var field = context.Render<ClayTime>(p => p.Add(x => x.Value, time));
        var picker = field.FindComponent<ClayConfirmedTimeInput>();
        await field.InvokeAsync(async () =>
        {
            await picker.Instance.PickerOpened.InvokeAsync();
            await picker.Instance.Submitted.InvokeAsync(time);
            await picker.Instance.Submitted.InvokeAsync(time);
            await picker.Instance.PickerClosed.InvokeAsync();
        });
        Assert.Single(context.JSInterop.Invocations["claimPopupSelection"]);
        Assert.Equal(21L, Assert.Single(context.JSInterop.Invocations["completePopupSelection"]).Arguments[1]);
    }

    private static BunitContext Setup(long? request)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js")
            .Setup<long?>("claimPopupSelection", _ => true).SetResult(request);
        return context;
    }
}
