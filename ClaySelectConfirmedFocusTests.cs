using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет привязку фокуса к подтверждённому результату настоящего ClaySelect.</summary>
public sealed class ClaySelectConfirmedFocusTests
{
    private const string ModulePath = "./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js";
    private static readonly Item[] Items = [new(1, "Альфа"), new(2, "Бета")];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClickOrEnterCompletesFocusOnlyAfterOwnerCallback(bool keyboard)
    {
        await using var fixture = NewContext();
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var field = Render(fixture, value => { received.TrySetResult(value); return callback.Task; });
        field.Find("button.clay-select-button").Click();
        var selection = ChooseAsync(field, keyboard);
        try
        {
            Assert.Equal(1, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Single(fixture.JSInterop.Invocations["captureSelectionAdvance"]);
            Assert.Empty(fixture.JSInterop.Invocations["completeSelectionAdvance"]);
            callback.SetResult();
            await selection;
            Assert.Single(fixture.JSInterop.Invocations["completeSelectionAdvance"]);
        }
        finally { callback.TrySetResult(); }
    }

    [Fact]
    public async Task ReboundOwnerAfterCallbackDoesNotAdvance()
    {
        await using var fixture = NewContext();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var field = Render(fixture, _ => { started.SetResult(); return waiting.Task; });
        field.Find("button.clay-select-button").Click();
        var selection = ChooseAsync(field, keyboard: false);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            field.Render(a => a.Add(x => x.Value, 2));
            waiting.SetResult();
            await selection;
            Assert.Empty(fixture.JSInterop.Invocations["completeSelectionAdvance"]);
        }
        finally { waiting.TrySetResult(); }
    }

    [Fact]
    public async Task SearchEscapeAndProgrammaticValueNeverCaptureAdvance()
    {
        await using var fixture = NewContext();
        var field = Render(fixture, _ => Task.CompletedTask);
        await field.Find("input").InputAsync("Альфа");
        await field.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await field.Find("input").InputAsync("что-то ещё");
        await field.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        field.Render(a => a.Add(x => x.Value, 2));
        Assert.Empty(fixture.JSInterop.Invocations["captureSelectionAdvance"]);
        Assert.Empty(fixture.JSInterop.Invocations["completeSelectionAdvance"]);
    }

    [Fact]
    public async Task DefaultOffDoesNotRequestAFocusLease()
    {
        await using var fixture = NewContext();
        var field = Render(fixture, _ => Task.CompletedTask, enabled: false);
        field.Find("button.clay-select-button").Click();
        await ChooseAsync(field, keyboard: false);
        Assert.Equal(1, field.Instance.Value);
        Assert.Empty(fixture.JSInterop.Invocations["captureSelectionAdvance"]);
        Assert.Empty(fixture.JSInterop.Invocations["completeSelectionAdvance"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvancedSearchAdvancesOnlyAfterAcceptedAcknowledgement(bool accept)
    {
        await using var fixture = NewContext();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var field = Render(fixture, _ => Task.CompletedTask, advanced: async context =>
        {
            var choice = accept
                ? ClaySelectExternalResult<Item, int?>.FromItem(Items[0])
                : ClaySelectExternalResult<Item, int?>.FromValue(9);
            var result = await context.ApplyAsync(choice);
            Assert.Equal(accept ? ClaySelectReturnDecision.Accepted : ClaySelectReturnDecision.Rejected, result);
            accepted.SetResult();
            await finish.Task;
            return null;
        });
        field.Find("button.clay-select-button").Click();
        var search = field.Find(".clay-select-advanced").ClickAsync();
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(fixture.JSInterop.Invocations["completeSelectionAdvance"]);
            finish.SetResult();
            await search;
            Assert.Equal(accept ? 1 : 0, fixture.JSInterop.Invocations["completeSelectionAdvance"].Count);
        }
        finally { finish.TrySetResult(); }
    }

    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule(ModulePath)
            .Setup<long?>("captureSelectionAdvance", _ => true).SetResult(7);
        return context;
    }

    [Fact]
    public async Task MultipleAcknowledgementAppliesOnlyOneCompleteSet()
    {
        await using var fixture = NewContext();
        await using var service = new ClaySelectAdvancedSearchService(fixture.JSInterop.JSRuntime);
        var applied = new List<int[]>();
        var pending = service.OpenAcknowledgedMultipleIdsAsync("http://localhost:5030/search", (values, _) =>
        {
            applied.Add(values);
            return Task.FromResult(ClaySelectReturnDecision.Accepted);
        });
        var request = (string)fixture.JSInterop.Invocations["claySelectAdvancedSearch.open"].Single().Arguments[1]!;
        Assert.Equal("Rejected", await service.ApplyAsync(request, 1, JsonSerializer.SerializeToElement(2)));
        Assert.Empty(applied);
        var fullSelection = JsonSerializer.SerializeToElement(new[] { 5, 2, 2 });
        Assert.Equal("Accepted", await service.ApplyAsync(request, 2, fullSelection));
        Assert.True(await pending);
        Assert.Equal([2, 5], Assert.Single(applied));
        Assert.Empty(fixture.JSInterop.Invocations["completeSelectionAdvance"]);
    }

    private static IRenderedComponent<ClaySelect<Item, int?>> Render(BunitContext context, Func<int?, Task> selected,
        Func<ClaySelectAdvancedSearchContext, Task<ClaySelectExternalResult<Item, int?>?>>? advanced = null,
        bool enabled = true) =>
        context.Render<ClaySelect<Item, int?>>(parameters => parameters
            .Add(field => field.Options, new ClaySelectOptions { AdvanceFocusOnSelection = enabled, AlwaysShowAdvancedSearch = true })
            .Add(field => field.Items, Items)
            .Add(field => field.ValueSelector, item => item.Id)
            .Add(field => field.TextSelector, item => item.Name)
            .Add(field => field.AdvancedSearch, advanced)
            .Add(field => field.ValueChanged, EventCallback.Factory.Create<int?>(new object(), selected)));

    private static Task ChooseAsync(IRenderedComponent<ClaySelect<Item, int?>> field, bool keyboard) => keyboard
        ? field.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" })
        : field.Find("[role='option']").ClickAsync();

    private sealed record Item(int Id, string Name);
}
