using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки подключения общей клавиатуры редактора к интерактивному рендеру.</summary>
public sealed class ClayEditorFocusScopeTests
{
    /// <summary>Неготовая область регистрируется, а последующий рендер сообщает готовность той же области.</summary>
    [Fact]
    public async Task ScopeSignalsReadinessAndDetaches()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js");
        module.SetupVoid("update", _ => true).SetVoidResult();
        module.SetupVoid("detach", _ => true).SetVoidResult();
        var scope = context.Render<ClayEditorFocusScope>(parameters => parameters
            .Add(component => component.Label, "Редактор узла")
            .Add(component => component.AdvanceOnEnter, true)
            .AddChildContent("<input />"));

        Assert.Equal("Редактор узла", scope.Find("[data-clay-edit-scope]").GetAttribute("aria-label"));
        scope.Render(parameters => parameters.Add(component => component.Ready, true));
        scope.Render();
        var updates = module.Invocations.Where(invocation => invocation.Identifier == "update").ToArray();
        Assert.Collection(updates,
            invocation => Assert.False(Assert.IsType<bool>(invocation.Arguments[1])),
            invocation => Assert.True(Assert.IsType<bool>(invocation.Arguments[1])));
        Assert.Equal(updates[0].Arguments[0], updates[1].Arguments[0]);

        await context.DisposeComponentsAsync();
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "detach");
    }
}
