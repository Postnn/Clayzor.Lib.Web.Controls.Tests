using Bunit;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Компонентные тесты основного сценария <see cref="ClaySelect{TItem, TValue}"/>.
/// </summary>
public sealed class ClaySelectTests : IDisposable
{
    private static readonly List<TestItem> Items =
    [
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Bravo" },
        new() { Id = 3, Name = "Charlie" },
    ];

    private readonly TestContext _ctx = new();

    /// <summary>
    /// Регистрирует сервисы MudBlazor для наследника MudFormComponent.
    /// </summary>
    public ClaySelectTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Освобождает контекст рендеринга.
    /// </summary>
    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Выбор из статического списка возвращает и ID, и объект.
    /// </summary>
    [Fact]
    public void StaticSelection_RaisesValueAndItemCallbacks()
    {
        int? value = null;
        TestItem? selected = null;
        var cut = Render(parameters => parameters
            .Add(component => component.ValueChanged, newValue => value = newValue)
            .Add(component => component.SelectedItemChanged, item => selected = item));

        cut.Find(".clay-select-button").Click();
        cut.FindAll(".clay-select-option")[1].Click();

        Assert.Equal(2, value);
        Assert.Same(Items[1], selected);
        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Статический источник ограничивает выдачу и сообщает о дополнительных результатах.
    /// </summary>
    [Fact]
    public void StaticSearch_AppliesLimitAndHasMore()
    {
        var cut = Render(options: new ClaySelectOptions { MaxItems = 2 });

        cut.Find(".clay-select-button").Click();

        Assert.Equal(2, cut.FindAll(".clay-select-option").Count);
        Assert.Contains("Есть ещё результаты", cut.Markup);
    }

    /// <summary>
    /// При несовпадении Value имеет приоритет над переданным SelectedItem.
    /// </summary>
    [Fact]
    public void ValueTakesPriorityOverSelectedItem()
    {
        var cut = Render(parameters => parameters
            .Add(component => component.Value, 2)
            .Add(component => component.SelectedItem, Items[0]));

        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Поздний ответ старого поиска не заменяет результаты нового запроса.
    /// </summary>
    [Fact]
    public void StaleSearch_DoesNotOverwriteNewResults()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source));

        cut.Find("input").Input("a");
        cut.Find("input").Input("b");
        source.CompleteSearch("b", Items[1]);
        cut.WaitForAssertion(() => Assert.Contains("Bravo", cut.Markup));
        source.CompleteSearch("a", Items[0]);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Alpha", cut.Markup));
    }

    /// <summary>
    /// Позднее восстановление старого ID не заменяет объект нового ID.
    /// </summary>
    [Fact]
    public void StaleResolve_DoesNotOverwriteNewValue()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.Value, 1));

        cut.Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.Value, 2));
        source.CompleteResolve(2, Items[1]);
        cut.WaitForAssertion(() => Assert.Equal("Bravo", cut.Find("input").GetAttribute("value")));
        source.CompleteResolve(1, Items[0]);
        cut.WaitForAssertion(() => Assert.Equal("Bravo", cut.Find("input").GetAttribute("value")));
    }

    /// <summary>
    /// Отмена расширенного поиска сохраняет прежний выбор.
    /// </summary>
    [Fact]
    public void CancelledAdvancedSearch_PreservesSelection()
    {
        var cut = Render(parameters => parameters
            .Add(component => component.Value, 1)
            .Add(component => component.AdvancedSearch,
                _ => Task.FromResult<ClaySelectExternalResult<TestItem, int?>?>(null)),
            new ClaySelectOptions { AlwaysShowAdvancedSearch = true });

        cut.Find(".clay-select-button").Click();
        cut.Find(".clay-select-advanced").Click();

        Assert.Equal("Alpha", cut.Find("input").GetAttribute("value"));
        Assert.Equal(1, cut.Instance.Value);
    }

    /// <summary>
    /// Расширенный поиск может вернуть только ID, объект восстанавливается из Items.
    /// </summary>
    [Fact]
    public void AdvancedSearchValue_ResolvesStaticItem()
    {
        TestItem? selected = null;
        var cut = Render(parameters => parameters
            .Add(component => component.SelectedItemChanged, item => selected = item)
            .Add(component => component.AdvancedSearch,
                _ => Task.FromResult<ClaySelectExternalResult<TestItem, int?>?>(
                    ClaySelectExternalResult<TestItem, int?>.FromValue(2))),
            new ClaySelectOptions { AlwaysShowAdvancedSearch = true });

        cut.Find(".clay-select-button").Click();
        cut.Find(".clay-select-advanced").Click();

        Assert.Same(Items[1], selected);
        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// MudForm считает Required по выбранному ID, а не по поисковому тексту.
    /// </summary>
    [Fact]
    public async Task MudFormRequired_ValidatesSelectedId()
    {
        var form = _ctx.Render<MudForm>(parameters => parameters.Add(
            component => component.ChildContent,
            BuildRequiredSelect()));

        await form.InvokeAsync(() => form.Instance.ValidateAsync());
        Assert.False(form.Instance.IsValid);

        form.Find(".clay-select-button").Click();
        form.Find(".clay-select-option").Click();
        await form.InvokeAsync(() => form.Instance.ValidateAsync());
        Assert.True(form.Instance.IsValid);
    }

    private IRenderedComponent<ClaySelect<TestItem, int?>> Render(
        Action<ComponentParameterCollectionBuilder<ClaySelect<TestItem, int?>>>? configure = null,
        ClaySelectOptions? options = null)
    {
        return _ctx.Render<ClaySelect<TestItem, int?>>(parameters =>
        {
            parameters
                .Add(component => component.Items, Items)
                .Add(component => component.ValueSelector, item => (int?)item.Id)
                .Add(component => component.TextSelector, item => item.Name)
                .Add(component => component.Options, options ?? new ClaySelectOptions { DebounceInterval = 0 });
            configure?.Invoke(parameters);
        });
    }

    private RenderFragment BuildRequiredSelect() => builder =>
    {
        builder.OpenComponent<ClaySelect<TestItem, int?>>(0);
        builder.AddAttribute(1, "Items", Items);
        builder.AddAttribute(2, "ValueSelector", (Func<TestItem, int?>)(item => item.Id));
        builder.AddAttribute(3, "TextSelector", (Func<TestItem, string>)(item => item.Name));
        builder.AddAttribute(4, "Options", new ClaySelectOptions { Required = true, DebounceInterval = 0 });
        builder.CloseComponent();
    };

    private sealed class TestItem
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class DeferredSource : IClaySelectDataSource<TestItem, int?>
    {
        private readonly Dictionary<string, TaskCompletionSource<ClaySelectResult<TestItem>>> _searches = new();
        private readonly Dictionary<int, TaskCompletionSource<TestItem?>> _resolves = new();

        public Task<ClaySelectResult<TestItem>> SearchAsync(
            ClaySelectSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<ClaySelectResult<TestItem>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _searches[request.SearchText] = completion;
            return completion.Task;
        }

        public Task<TestItem?> GetByIdAsync(int? id, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<TestItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resolves[id!.Value] = completion;
            return completion.Task;
        }

        public void CompleteSearch(string text, TestItem item) =>
            _searches[text].TrySetResult(new ClaySelectResult<TestItem> { Items = [item] });

        public void CompleteResolve(int id, TestItem item) => _resolves[id].TrySetResult(item);
    }
}
