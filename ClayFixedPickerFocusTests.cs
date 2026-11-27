using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет, что фиксированный список завершает переход после принятия владельцем.</summary>
public sealed class ClayFixedPickerFocusTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DropDownAdvancesOnlyWithGestureAndAcceptedValue(bool hasGesture, bool accepts)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        const string modulePath = "./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js";
        context.JSInterop.SetupModule(modulePath).Setup<long?>("claimNativeSelectionAdvance", _ => true)
            .SetResult(hasGesture ? 41L : null);
        context.Render<MudPopoverProvider>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offered = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IRenderedComponent<ClayDropDownList<Option, int?>>? field = null;
        field = context.Render<ClayDropDownList<Option, int?>>(p => p
            .Add(x => x.Options, new ClayDropDownListOptions { AdvanceFocusOnSelection = true })
            .Add(x => x.Items, [new Option(1), new Option(2)])
            .Add(x => x.Value, 1)
            .Add(x => x.ValueSelector, option => option.Id)
            .Add(x => x.TextSelector, option => option.Id.ToString())
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<int?>(new object(), async value =>
            {
                offered.TrySetResult(value);
                await release.Task;
                if (accepts) field!.Render(parameters => parameters.Add(x => x.Value, value));
            })));
        var target = field.Find("[role=combobox][data-clay-fixed-picker]");
        var key = target.GetAttribute("data-clay-fixed-picker");
        var choice = field.InvokeAsync(() => field.FindComponent<MudSelect<int?>>().Instance.ValueChanged.InvokeAsync(2));
        try
        {
            Assert.Equal(2, await offered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(key, Assert.Single(context.JSInterop.Invocations["claimNativeSelectionAdvance"]).Arguments[0]);
            Assert.Empty(context.JSInterop.Invocations["completeNativeSelectionAdvance"]);
            release.SetResult();
            await choice;
            Assert.Equal(hasGesture && accepts ? 1 : 0, context.JSInterop.Invocations["completeNativeSelectionAdvance"].Count);
        }
        finally { release.TrySetResult(); }
    }

    private sealed record Option(int Id);
}
