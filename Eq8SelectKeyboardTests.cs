using Bunit;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки клавиатурной доступности служебных действий ClaySelect EQ8.</summary>
public sealed class Eq8SelectKeyboardTests : IDisposable
{
    private readonly TestContext _ctx = new();

    /// <summary>Настраивает MudBlazor и нестрогий JSInterop.</summary>
    public Eq8SelectKeyboardTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Освобождает renderer и зарегистрированные ссылки.</summary>
    public void Dispose() => _ctx.Dispose();

    /// <summary>Внутренний переход фокуса по Tab не закрывает popup с Advanced и Create.</summary>
    [Fact]
    public void InternalTabFocus_KeepsServiceActionsOpen()
    {
        _ctx.JSInterop.Setup<bool>("claySelect.containsFocus", _ => true).SetResult(true);
        var cut = Render([], options: new ClaySelectOptions { AlwaysShowAdvancedSearch = true });
        cut.Find(".clay-select-button").Click();
        Assert.NotNull(cut.Find(".clay-select-advanced"));
        Assert.NotNull(cut.Find(".clay-select-create"));

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Tab" });
        cut.Find("input").Blur();

        Assert.NotNull(cut.Find(".clay-select-popup"));
        Assert.NotNull(cut.Find(".clay-select-advanced"));
        Assert.NotNull(cut.Find(".clay-select-create"));
    }

    /// <summary>Поздний ответ containsFocus не запускает blur после внешней смены выбора.</summary>
    [Fact]
    public async Task StaleBlur_DoesNotSearchAfterExternalValueChange()
    {
        var containsFocus = _ctx.JSInterop.Setup<bool>("claySelect.containsFocus", _ => true);
        var source = new TrackingSource();
        var items = new[] { new TestItem(1, "Alpha"), new TestItem(2, "Bravo") };
        var cut = Render(items, parameters => parameters.Add(component => component.DataSource, source));
        cut.Find(".clay-select-button").Click();
        Assert.Equal(1, source.SearchCalls);

        cut.Find("input").Blur();
        cut.Render(parameters => parameters
            .Add(component => component.Value, 2)
            .Add(component => component.SelectedItem, items[1]));
        containsFocus.SetResult(false);
        await cut.InvokeAsync(() => System.Threading.Tasks.Task.CompletedTask);
        await cut.InvokeAsync(() => System.Threading.Tasks.Task.CompletedTask);

        Assert.Equal(1, source.SearchCalls);
        Assert.Equal(2, cut.Instance.Value);
        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>Escape на сфокусированном служебном действии сохраняет восстановление backup.</summary>
    [Fact]
    public void EscapeFromServiceAction_RestoresPreviousSelection()
    {
        var cut = Render([new TestItem(1, "Alpha"), new TestItem(2, "Bravo")],
            parameters => parameters
                .Add(component => component.Value, 1)
                .Add(component => component.SelectedItem, new TestItem(1, "Alpha")),
            new ClaySelectOptions { AlwaysShowAdvancedSearch = true });
        cut.Find("input").Input("Bravo");
        cut.Find(".clay-select-button").Click();

        cut.Find(".clay-select-advanced").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(1, cut.Instance.Value);
        Assert.Equal("Alpha", cut.Find("input").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".clay-select-popup"));
    }

    /// <summary>При клавиатурной навигации активная option прокручивается в видимую область.</summary>
    [Fact]
    public void ArrowNavigation_ScrollsActiveOptionIntoView()
    {
        var cut = Render([new TestItem(1, "Alpha"), new TestItem(2, "Bravo")]);
        cut.Find(".clay-select-button").Click();
        var listboxId = cut.Find(".clay-select-popup").Id;

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        var invocation = _ctx.JSInterop.Invocations.Last(call => call.Identifier == "claySelect.ensureVisible");
        Assert.Equal($"{listboxId}-option-1", invocation.Arguments[1]);
    }

    /// <summary>Сфокусированное служебное действие также остаётся в viewport.</summary>
    [Fact]
    public void FocusedServiceAction_IsScrolledIntoView()
    {
        var cut = Render([], options: new ClaySelectOptions { AlwaysShowAdvancedSearch = true });
        cut.Find(".clay-select-button").Click();
        var listboxId = cut.Find(".clay-select-popup").Id;

        cut.Find(".clay-select-advanced").Focus();

        var invocation = _ctx.JSInterop.Invocations.Last(call => call.Identifier == "claySelect.ensureVisible");
        Assert.Equal($"{listboxId}-advanced", invocation.Arguments[1]);
    }

    private Bunit.IRenderedComponent<ClaySelect<TestItem, int?>> Render(
        IReadOnlyList<TestItem> items,
        Action<ComponentParameterCollectionBuilder<ClaySelect<TestItem, int?>>>? configure = null,
        ClaySelectOptions? options = null) =>
        _ctx.Render<ClaySelect<TestItem, int?>>(parameters =>
        {
            parameters
                .Add(component => component.Items, items)
                .Add(component => component.ValueSelector, item => (int?)item.Id)
                .Add(component => component.TextSelector, item => item.Name)
                .Add(component => component.Options, options ?? new ClaySelectOptions())
                .Add(component => component.AdvancedSearch,
                    _ => System.Threading.Tasks.Task.FromResult<ClaySelectExternalResult<TestItem, int?>?>(null))
                .Add(component => component.CreateItem,
                    _ => System.Threading.Tasks.Task.FromResult<ClaySelectExternalResult<TestItem, int?>?>(null));
            configure?.Invoke(parameters);
        });

    private sealed class TrackingSource : IClaySelectDataSource<TestItem, int?>
    {
        public int SearchCalls { get; private set; }

        public System.Threading.Tasks.Task<ClaySelectResult<TestItem>> SearchAsync(
            ClaySelectSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return System.Threading.Tasks.Task.FromResult(new ClaySelectResult<TestItem>());
        }

        public System.Threading.Tasks.Task<TestItem?> GetByIdAsync(
            int? id,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<TestItem?>(null);
    }

    private sealed record TestItem(int Id, string Name);
}
