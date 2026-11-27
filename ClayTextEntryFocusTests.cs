using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет принятие текстового значения и валидацию до перевода фокуса.</summary>
public sealed class ClayTextEntryFocusTests
{
    [Theory]
    [InlineData("101", true, true)]
    [InlineData("101", false, false)]
    [InlineData("", true, false)]
    public async Task EnterWaitsForAcceptedBindingAndValidValue(string text, bool accepted, bool advances)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js")
            .Setup<ClayTextEntry?>("claimTextEntry", _ => true).SetResult(new ClayTextEntry(17, text));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IRenderedComponent<ClayTextBox>? field = null;
        field = context.Render<ClayTextBox>(p => p
            .Add(x => x.Options, new ClayTextBoxOptions { Immediate = false, Required = true })
            .Add(x => x.Value, "100")
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<string?>(new object(), async value =>
            {
                started.TrySetResult();
                await release.Task;
                if (accepted) field!.Render(parameters => parameters.Add(x => x.Value, value));
            })));
        var input = field.FindComponent<MudTextField<string>>();
        var change = field.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync(text));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await field.InvokeAsync(() => input.Instance.OnKeyDown.InvokeAsync(new KeyboardEventArgs { Key = "Enter" }));
            Assert.Empty(context.JSInterop.Invocations["claimTextEntry"]);
            release.SetResult();
            await change;
            Assert.Single(context.JSInterop.Invocations["claimTextEntry"]);
            Assert.Equal(advances ? 1 : 0, context.JSInterop.Invocations["completeTextEntry"].Count);
            Assert.Equal(accepted ? text : "100", field.Instance.Value);
            if (accepted && text.Length == 0) Assert.True(input.Instance.HasErrors);
        }
        finally { release.TrySetResult(); }
    }
}
