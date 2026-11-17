using Bunit;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
    /// После выбора кнопка просмотра открывает карточку вместо popup поиска.
    /// </summary>
    [Fact]
    public void SelectedItemWithViewHandler_OpensEntityView()
    {
        TestItem? viewed = null;
        var cut = Render(parameters => parameters
            .Add(component => component.Value, 1)
            .Add(component => component.SelectedItem, Items[0])
            .Add(component => component.ViewItem, item =>
            {
                viewed = item;
                return Task.CompletedTask;
            }));

        var button = cut.Find(".clay-select-view-button");
        Assert.Equal("Просмотреть сущность", button.GetAttribute("aria-label"));
        button.Click();

        Assert.Same(Items[0], viewed);
        Assert.Empty(cut.FindAll(".clay-select-popup"));
        Assert.Equal("Alpha", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Escape после изменения текста восстанавливает выбранные ранее ID и объект.
    /// </summary>
    [Fact]
    public void EscapeAfterInput_RestoresPreviousSelection()
    {
        var cut = Render(parameters => parameters
            .Add(component => component.Value, 1)
            .Add(component => component.SelectedItem, Items[0]));

        cut.Find("input").Input("Bravo");
        Assert.Null(cut.Instance.Value);
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(1, cut.Instance.Value);
        Assert.Equal("Alpha", cut.Find("input").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".clay-select-popup"));
    }

    /// <summary>
    /// При неизменном тексте выбранного элемента расширенный поиск получает пустой запрос.
    /// </summary>
    [Fact]
    public void AdvancedSearch_DoesNotReuseSelectedText()
    {
        string? receivedText = null;
        var cut = Render(parameters => parameters
            .Add(component => component.Value, 1)
            .Add(component => component.SelectedItem, Items[0])
            .Add(component => component.AdvancedSearch, context =>
            {
                receivedText = context.SearchText;
                return Task.FromResult<ClaySelectExternalResult<TestItem, int?>?>(null);
            }),
            new ClaySelectOptions { AlwaysShowAdvancedSearch = true });

        cut.Find(".clay-select-button").Click();
        cut.Find(".clay-select-advanced").Click();

        Assert.Equal(string.Empty, receivedText);
    }

    /// <summary>
    /// Enter на тексте уже выбранного элемента не повторяет выбор и не открывает popup.
    /// </summary>
    [Fact]
    public void EnterOnSelectedValue_DoesNotSelectAgain()
    {
        var valueNotifications = 0;
        var itemNotifications = 0;
        var cut = Render(parameters => parameters
            .Add(component => component.Value, 1)
            .Add(component => component.SelectedItem, Items[0])
            .Add(component => component.ValueChanged, _ => valueNotifications++)
            .Add(component => component.SelectedItemChanged, _ => itemNotifications++));

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(0, valueNotifications);
        Assert.Equal(0, itemNotifications);
        Assert.Empty(cut.FindAll(".clay-select-popup"));
        Assert.Equal("Alpha", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Наведение мыши не вызывает серверный рендер и не меняет клавиатурную активную строку.
    /// </summary>
    [Fact]
    public void MouseOver_DoesNotRerenderOrChangeKeyboardActiveOption()
    {
        var cut = Render();
        cut.Find(".clay-select-button").Click();
        var options = cut.FindAll(".clay-select-option");
        var markup = cut.Markup;

        Assert.Throws<MissingEventHandlerException>(() => options[1].MouseOver());

        Assert.Equal(markup, cut.Markup);
        Assert.Contains("clay-select-option--active", options[0].ClassName);
        Assert.True(options[0].HasAttribute("aria-selected"));
        Assert.DoesNotContain("clay-select-option--active", options[1].ClassName);
        Assert.False(options[1].HasAttribute("aria-selected"));
    }

    /// <summary>
    /// Отмена во время ожидания closeOthers запрещает старому поиску открывать popup и обращаться к источнику.
    /// </summary>
    [Fact]
    public async Task CancelledCloseOthers_DoesNotOpenPopupOrSearch()
    {
        var closeOthers = _ctx.JSInterop.SetupVoid("claySelect.closeOthers", _ => true);
        var source = new DeferredSource();
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source));
        cut.Find(".clay-select-button").Click();

        cut.Find("input").Input("Bravo");
        closeOthers.SetVoidResult();
        await cut.InvokeAsync(() => Task.CompletedTask);
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Empty(cut.FindAll(".clay-select-popup"));
        Assert.False(source.HasSearch(string.Empty));
        Assert.False(source.HasSearch("Bravo"));
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
    /// Ввод текста не запускает поиск и не открывает список до явного действия пользователя.
    /// </summary>
    [Fact]
    public void Input_DoesNotSearchAutomatically()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source));

        cut.Find("input").Input("Bravo");

        Assert.False(source.HasSearch("Bravo"));
        Assert.Empty(cut.FindAll(".clay-select-popup"));
        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Enter запускает поиск именно введённого текста.
    /// </summary>
    [Fact]
    public async Task Enter_SearchesEnteredText()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source),
            new ClaySelectOptions { AutoSelectExactMatch = false });

        cut.Find("input").Input("Bravo");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.True(source.HasSearch("Bravo"));
        await CompleteSearchAndFlushAsync(cut, source, "Bravo", Items[1]);
        Assert.Contains("Bravo", cut.Markup);
        Assert.DoesNotContain("Alpha", cut.Markup);
    }

    /// <summary>
    /// Кнопка с многоточием использует введённый текст, а не подменяет его пустым запросом.
    /// </summary>
    [Fact]
    public void PopupButton_SearchesEnteredText()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source));

        cut.Find("input").Input("Bravo");
        cut.Find(".clay-select-button").Click();

        Assert.True(source.HasSearch("Bravo"));
        Assert.False(source.HasSearch(string.Empty));
    }

    /// <summary>
    /// Потеря фокуса запускает поиск, но не оставляет открытый popup.
    /// </summary>
    [Fact]
    public async Task Blur_SearchesWithoutOpeningPopup()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source));

        cut.Find("input").Input("Bravo");
        cut.Find("input").Blur();
        Assert.True(source.HasSearch("Bravo"));
        await CompleteSearchAndFlushAsync(cut, source, "Bravo", Items[1]);

        Assert.Equal(2, cut.Instance.Value);
        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".clay-select-popup"));
    }

    /// <summary>
    /// Поздний ответ старого поиска не заменяет результаты нового запроса.
    /// </summary>
    [Fact]
    public async Task StaleSearch_DoesNotOverwriteNewResults()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source));

        cut.Find("input").Input("a");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.Find("input").Input("b");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        await CompleteSearchAndFlushAsync(cut, source, "b", Items[1]);
        cut.WaitForAssertion(() => Assert.Contains("Bravo", cut.Markup));
        await CompleteSearchAndFlushAsync(cut, source, "a", Items[0]);
        Assert.DoesNotContain("Alpha", cut.Markup);
    }

    /// <summary>
    /// Позднее восстановление старого ID не заменяет объект нового ID.
    /// </summary>
    [Fact]
    public async Task StaleResolve_DoesNotOverwriteNewValue()
    {
        var source = new DeferredSource();
        var cut = Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.Value, 1));

        cut.Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.Value, 2));
        await CompleteResolveAndFlushAsync(cut, source, 2, Items[1]);
        cut.WaitForAssertion(() => Assert.Equal("Bravo", cut.Find("input").GetAttribute("value")));
        await CompleteResolveAndFlushAsync(cut, source, 1, Items[0]);
        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Повторный рендер с неизменным пустым Value не стирает введённый текст до Enter.
    /// </summary>
    [Fact]
    public async Task ParentRenderBeforeEnter_PreservesTextAndSearch()
    {
        var source = new DeferredSource();
        var options = new ClaySelectOptions { AutoSelectExactMatch = false };
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source), options);

        cut.Find("input").Input("a");
        cut.Render(parameters => parameters
            .Add(component => component.Items, Items)
            .Add(component => component.ValueSelector, item => (int?)item.Id)
            .Add(component => component.TextSelector, item => item.Name)
            .Add(component => component.Options, options)
            .Add(component => component.DataSource, source)
            .Add(component => component.Value, null));

        Assert.Equal("a", cut.Find("input").GetAttribute("value"));
        Assert.False(source.HasSearch("a"));
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.True(source.HasSearch("a"));
        await CompleteSearchAndFlushAsync(cut, source, "a", Items[0]);
        Assert.Contains("Alpha", cut.Markup);
    }

    /// <summary>
    /// Клавиатура не активирует и не выбирает старую выдачу во время загрузки.
    /// </summary>
    [Fact]
    public void KeyboardDuringLoading_DoesNotSelectOldResult()
    {
        var source = new DeferredSource();
        TestItem? selected = null;
        var cut = Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.SelectedItemChanged, item => selected = item),
            new ClaySelectOptions { AutoSelectExactMatch = false });

        cut.Find(".clay-select-button").Click();
        cut.Find("input").Input("b");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.True(source.HasSearch("b"));
        Assert.Contains("Загрузка", cut.Markup);
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Null(selected);
        Assert.Null(cut.Instance.Value);
    }

    /// <summary>
    /// Внешняя очистка Value один раз очищает привязанный объект родителя.
    /// </summary>
    [Fact]
    public void ExternalClear_NotifiesSelectedItemOnce()
    {
        var notifications = new List<TestItem?>();
        var cut = Render(parameters => parameters
            .Add(component => component.Value, 1)
            .Add(component => component.SelectedItem, Items[0])
            .Add(component => component.SelectedItemChanged, item => notifications.Add(item)));

        void RenderCleared() => cut.Render(parameters => parameters
            .Add(component => component.Items, Items)
            .Add(component => component.ValueSelector, item => (int?)item.Id)
            .Add(component => component.TextSelector, item => item.Name)
            .Add(component => component.Options, new ClaySelectOptions())
            .Add(component => component.Value, null)
            .Add(component => component.SelectedItem, Items[0])
            .Add(component => component.SelectedItemChanged, item => notifications.Add(item)));

        RenderCleared();
        RenderCleared();

        Assert.Single(notifications);
        Assert.Null(notifications[0]);
        Assert.Equal(string.Empty, cut.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Сигнал браузера о внешнем клике или потере фокуса закрывает popup.
    /// </summary>
    [Fact]
    public async Task OutsideInteraction_ClosesPopup()
    {
        var cut = Render();
        cut.Find(".clay-select-button").Click();
        Assert.NotEmpty(cut.FindAll(".clay-select-popup"));

        await cut.InvokeAsync(() => cut.Instance.ClosePopupFromOutsideAsync());

        Assert.Empty(cut.FindAll(".clay-select-popup"));
    }

    /// <summary>
    /// Перед открытием списка компонент централизованно закрывает остальные экземпляры.
    /// </summary>
    [Fact]
    public void OpeningPopup_ClosesOtherSelects()
    {
        var cut = Render();

        cut.Find(".clay-select-button").Click();

        var invocation = _ctx.JSInterop.Invocations.Single(item => item.Identifier == "claySelect.closeOthers");
        Assert.Equal(cut.Find("input").Id, invocation.Arguments[0]);
    }

    /// <summary>
    /// Popup привязан к полю, поэтому валидационное сообщение не меняет его вертикальный якорь.
    /// </summary>
    [Fact]
    public async Task ValidationMessage_DoesNotChangePopupAnchor()
    {
        var cut = Render(options: new ClaySelectOptions { Required = true });
        await cut.InvokeAsync(() => cut.Instance.ValidateAsync());
        cut.Find(".clay-select-button").Click();

        Assert.NotEmpty(cut.FindAll(".clay-select-validation"));
        Assert.Equal("clay-select-field", cut.Find(".clay-select-popup").ParentElement?.ClassName);
    }

    /// <summary>
    /// Disabled и ReadOnly запрещают открытие списка.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DisabledOrReadOnly_PreventsOpening(bool disabled, bool readOnly)
    {
        var cut = Render(options: new ClaySelectOptions { Disabled = disabled, ReadOnly = readOnly });

        Assert.True(cut.Find(".clay-select-button").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".clay-select-popup"));
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

    private static async Task CompleteSearchAndFlushAsync(
        IRenderedComponent<ClaySelect<TestItem, int?>> cut,
        DeferredSource source,
        string text,
        TestItem item)
    {
        await cut.InvokeAsync(() => source.CompleteSearch(text, item));
        await cut.InvokeAsync(() => Task.CompletedTask);
    }

    private static async Task CompleteResolveAndFlushAsync(
        IRenderedComponent<ClaySelect<TestItem, int?>> cut,
        DeferredSource source,
        int id,
        TestItem item)
    {
        await cut.InvokeAsync(() => source.CompleteResolve(id, item));
        await cut.InvokeAsync(() => Task.CompletedTask);
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
                .Add(component => component.Options, options ?? new ClaySelectOptions());
            configure?.Invoke(parameters);
        });
    }

    private RenderFragment BuildRequiredSelect() => builder =>
    {
        builder.OpenComponent<ClaySelect<TestItem, int?>>(0);
        builder.AddAttribute(1, "Items", Items);
        builder.AddAttribute(2, "ValueSelector", (Func<TestItem, int?>)(item => item.Id));
        builder.AddAttribute(3, "TextSelector", (Func<TestItem, string>)(item => item.Name));
        builder.AddAttribute(4, "Options", new ClaySelectOptions { Required = true });
        builder.CloseComponent();
    };

    private sealed class TestItem
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class DeferredSource : IClaySelectDataSource<TestItem, int?>
    {
        private readonly List<string> _searchTexts = [];
        private readonly Dictionary<string, TaskCompletionSource<ClaySelectResult<TestItem>>> _searches = new();
        private readonly Dictionary<int, TaskCompletionSource<TestItem?>> _resolves = new();

        public Task<ClaySelectResult<TestItem>> SearchAsync(
            ClaySelectSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            _searchTexts.Add(request.SearchText);
            if (request.SearchText.Length == 0)
                return Task.FromResult(new ClaySelectResult<TestItem> { Items = Items });

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

        public bool HasSearch(string text) => _searchTexts.Contains(text);

        public void CompleteSearch(string text, TestItem item) =>
            _searches[text].TrySetResult(new ClaySelectResult<TestItem> { Items = [item] });

        public void CompleteResolve(int id, TestItem item) => _resolves[id].TrySetResult(item);
    }
}
