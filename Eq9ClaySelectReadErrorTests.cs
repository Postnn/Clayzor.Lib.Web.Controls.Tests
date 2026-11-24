using Bunit;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Компонентные регрессии failed/stale/retry и raw/typed разделения ClaySelect.</summary>
public sealed class Eq9ClaySelectReadErrorTests : IDisposable
{
    private readonly TestContext _ctx = new();

    /// <summary>Настраивает production ClaySelect с реальной разметкой и JSInterop.</summary>
    public Eq9ClaySelectReadErrorTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Освобождает renderer.</summary>
    public void Dispose() => _ctx.Dispose();

    /// <summary>Search fault сохраняет текст/ID; retry читает данные, но не делает auto-select по кнопке.</summary>
    [Fact]
    public void SearchFailure_RetryPreservesSelectionAndDoesNotAutoSelect()
    {
        var source = new ScriptedSource { NextSearchException = new InvalidOperationException("read failed") };
        var errors = new List<ClaySelectReadError>();
        var valueChanges = new List<int?>();
        var cut = Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.Value, 1)
            .Add(component => component.SelectedItem, new TestItem(1, "Alpha"))
            .Add(component => component.ValueChanged, value => valueChanges.Add(value))
            .Add(component => component.ReadError, error => errors.Add(error)), new ClaySelectOptions
        {
            AutoSelectSingle = true,
            Clearable = false,
        });
        cut.Find("input").Input("Bravo");
        cut.Find(".clay-select-button").Click();

        Assert.Equal(1, cut.Instance.Value);
        Assert.Equal("Bravo", cut.Find("input").GetAttribute("value"));
        Assert.NotNull(cut.Find(".clay-select-read-error"));
        Assert.Equal(ClaySelectReadOperation.Search, Assert.Single(errors).Operation);
        Assert.Empty(cut.FindAll(".clay-select-option"));

        cut.Find(".clay-select-retry").Click();

        Assert.Equal(2, source.SearchCalls);
        Assert.Equal(1, cut.Instance.Value);
        Assert.Empty(valueChanges);
        Assert.Empty(cut.FindAll(".clay-select-read-error"));
        Assert.Equal("Bravo", cut.Find(".clay-select-option").TextContent);
    }

    /// <summary>GetById fault не превращает ID в empty; retry разрешает тот же ID без ValueChanged и дублирования SelectedItemChanged.</summary>
    [Fact]
    public void ResolveFailure_RetryKeepsIdAndSelectionNotifications()
    {
        var source = new ScriptedSource { NextResolveException = new InvalidOperationException("denied") };
        var selected = new List<TestItem?>();
        var valueChanges = new List<int?>();
        var errors = new List<ClaySelectReadError>();
        var cut = Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.Value, 7)
            .Add(component => component.SelectedItemChanged, item => selected.Add(item))
            .Add(component => component.ValueChanged, value => valueChanges.Add(value))
            .Add(component => component.ReadError, error => errors.Add(error)));

        Assert.Equal(7, cut.Instance.Value);
        Assert.Equal("#7", cut.Find("input").GetAttribute("value"));
        Assert.Equal(ClaySelectReadOperation.Resolve, Assert.Single(errors).Operation);
        Assert.Empty(selected);

        cut.Find(".clay-select-retry").Click();

        Assert.Equal(2, source.ResolveCalls);
        Assert.Equal("Seven", cut.Find("input").GetAttribute("value"));
        Assert.Equal(7, cut.Instance.Value);
        Assert.Equal(7, Assert.Single(selected)!.Id);
        Assert.Empty(valueChanges);
    }

    /// <summary>Поздний Search fault после нового успеха не создаёт error state и не затирает результат.</summary>
    [Fact]
    public async Task StaleSearchFailure_DoesNotReplaceNewSuccess()
    {
        var source = new ScriptedSource { DeferSearches = true };
        var cut = Render(parameters => parameters.Add(component => component.DataSource, source));
        cut.Find("input").Input("a");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.Find("input").Input("b");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        await cut.InvokeAsync(() => source.CompleteSearch("b", new TestItem(2, "Bravo")));
        cut.WaitForAssertion(() => Assert.Equal("Bravo", cut.Find(".clay-select-option").TextContent));
        await cut.InvokeAsync(() => source.FailSearch("a", new InvalidOperationException("late fault")));
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Empty(cut.FindAll(".clay-select-read-error"));
        Assert.Equal("Bravo", cut.Find(".clay-select-option").TextContent);
    }

    /// <summary>Raw text restore не выбирает ID по подписи и не вызывает источник или selection events.</summary>
    [Fact]
    public async Task RestoreDraftText_DoesNotSelectByLabel()
    {
        var source = new ScriptedSource();
        var selected = new List<TestItem?>();
        var cut = Render(parameters => parameters
            .Add(component => component.DataSource, source)
            .Add(component => component.SelectedItemChanged, item => selected.Add(item)));

        await cut.InvokeAsync(() => cut.Instance.RestoreDraftTextAsync("Alpha"));

        Assert.Null(cut.Instance.Value);
        Assert.Equal("Alpha", cut.Find("input").GetAttribute("value"));
        Assert.Equal(0, source.SearchCalls);
        Assert.Equal(0, source.ResolveCalls);
        Assert.Empty(selected);
    }

    private IRenderedComponent<ClaySelect<TestItem, int?>> Render(
        Action<ComponentParameterCollectionBuilder<ClaySelect<TestItem, int?>>> configure,
        ClaySelectOptions? options = null) =>
        _ctx.Render<ClaySelect<TestItem, int?>>(parameters =>
        {
            parameters
                .Add(component => component.Items, [new TestItem(1, "Alpha"), new TestItem(2, "Bravo")])
                .Add(component => component.ValueSelector, item => (int?)item.Id)
                .Add(component => component.TextSelector, item => item.Name)
                .Add(component => component.Options, options ?? new ClaySelectOptions());
            configure(parameters);
        });

    private sealed record TestItem(int Id, string Name);

    private sealed class ScriptedSource : IClaySelectDataSource<TestItem, int?>
    {
        private readonly Dictionary<string, TaskCompletionSource<ClaySelectResult<TestItem>>> _deferred = new();

        public int SearchCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public Exception? NextSearchException { get; set; }
        public Exception? NextResolveException { get; set; }
        public bool DeferSearches { get; set; }

        public Task<ClaySelectResult<TestItem>> SearchAsync(
            ClaySelectSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            if (NextSearchException is not null)
            {
                var error = NextSearchException;
                NextSearchException = null;
                return Task.FromException<ClaySelectResult<TestItem>>(error);
            }
            if (DeferSearches)
            {
                var completion = new TaskCompletionSource<ClaySelectResult<TestItem>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _deferred[request.SearchText] = completion;
                return completion.Task;
            }
            return Task.FromResult(new ClaySelectResult<TestItem>
            {
                Items = [new TestItem(2, "Bravo")],
            });
        }

        public Task<TestItem?> GetByIdAsync(int? id, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            if (NextResolveException is not null)
            {
                var error = NextResolveException;
                NextResolveException = null;
                return Task.FromException<TestItem?>(error);
            }
            return Task.FromResult<TestItem?>(new TestItem(id!.Value, "Seven"));
        }

        public void CompleteSearch(string text, TestItem item) =>
            _deferred[text].TrySetResult(new ClaySelectResult<TestItem> { Items = [item] });

        public void FailSearch(string text, Exception error) => _deferred[text].TrySetException(error);
    }
}
