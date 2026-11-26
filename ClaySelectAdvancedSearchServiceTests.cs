using Clayzor.Lib.Web.Controls.Components.Select;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Проверяет централизованный протокол возврата из внешнего расширенного поиска.
/// </summary>
public sealed class ClaySelectAdvancedSearchServiceTests : IDisposable
{
    private readonly TestContext _ctx = new();

    /// <summary>Настраивает свободный JS interop для открытия окна.</summary>
    public ClaySelectAdvancedSearchServiceTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>Освобождает тестовый контекст.</summary>
    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Возвращённый по requestId ID преобразуется в результат FromValue для восстановления через источник.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ReturnsExternalValue()
    {
        var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        var pending = service.OpenAsync<Employee, int?>("http://localhost:5030/search");
        var invocation = _ctx.JSInterop.Invocations.Single(item => item.Identifier == "claySelectAdvancedSearch.open");
        var requestId = (string)invocation.Arguments[1]!;
        using var document = JsonDocument.Parse("42");

        await service.CompleteAsync(requestId, document.RootElement.Clone());
        var result = await pending;

        Assert.NotNull(result);
        Assert.False(result.ContainsItem);
        Assert.Equal(42, result.Value);
    }

    /// <summary>
    /// Закрытие окна завершает ожидание отменой без изменения выбора.
    /// </summary>
    [Fact]
    public async Task CancelAsync_ReturnsNull()
    {
        var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        var pending = service.OpenAsync<Employee, int?>("http://localhost:5030/search");
        var invocation = _ctx.JSInterop.Invocations.Single(item => item.Identifier == "claySelectAdvancedSearch.open");
        var requestId = (string)invocation.Arguments[1]!;

        await service.CancelAsync(requestId);

        Assert.Null(await pending);
        AssertCancelInvocation(requestId);
    }

    /// <summary>
    /// Отмена токена завершает C#-ожидание и удаляет браузерную часть запроса.
    /// </summary>
    [Fact]
    public async Task CancellationToken_CancelsBrowserRequest()
    {
        var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        using var cts = new CancellationTokenSource();
        var pending = service.OpenAsync<Employee, int?>("http://localhost:5030/search", cts.Token);
        var invocation = _ctx.JSInterop.Invocations.Single(item => item.Identifier == "claySelectAdvancedSearch.open");
        var requestId = (string)invocation.Arguments[1]!;

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        AssertCancelInvocation(requestId);
    }

    /// <summary>
    /// Освобождение сервиса очищает JS и завершает ожидание отменой.
    /// </summary>
    [Fact]
    public async Task Dispose_CancelsBrowserRequestAndCompletesPendingWait()
    {
        var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        var pending = service.OpenAsync<Employee, int?>("http://localhost:5030/search");
        var invocation = _ctx.JSInterop.Invocations.Single(item => item.Identifier == "claySelectAdvancedSearch.open");
        var requestId = (string)invocation.Arguments[1]!;

        await service.DisposeAsync();

        Assert.Null(await pending);
        AssertCancelInvocation(requestId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.OpenAsync<Employee, int?>("http://localhost:5030/search"));
    }

    /// <summary>Отклонение оставляет запрос, а повтор принятой ревизии возвращает тот же ack.</summary>
    [Fact]
    public async Task AcknowledgedRequestRetriesRejectedAndDoesNotReapplyAcceptedRevision()
    {
        await using var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        var attempts = 0;
        var accepted = new TaskCompletionSource<ClaySelectReturnDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = service.OpenAcknowledgedAsync<Employee, int?>("http://localhost:5030/search", (result, _) =>
        {
            attempts++;
            return result.Value == 42 ? accepted.Task : Task.FromResult(ClaySelectReturnDecision.Rejected);
        });
        var requestId = LastRequestId();
        var missing = JsonSerializer.SerializeToElement(10);
        var valid = JsonSerializer.SerializeToElement(42);

        Assert.Equal("Stale", await service.ApplyAsync("other", 1, valid));
        Assert.Equal("Rejected", await service.ApplyAsync(requestId, 1, missing));
        Assert.Equal("Rejected", await service.ApplyAsync(requestId, 1, missing));
        Assert.False(pending.IsCompleted);
        Assert.Equal("Stale", await service.ApplyAsync(requestId, 1, valid));

        var applied = service.ApplyAsync(requestId, 2, valid);
        var duplicate = service.ApplyAsync(requestId, 2, valid);
        Assert.False(pending.IsCompleted);
        Assert.False(duplicate.IsCompleted);
        accepted.SetResult(ClaySelectReturnDecision.Accepted);
        Assert.Equal("Accepted", await applied);
        Assert.Equal("Accepted", await duplicate);
        Assert.True(await pending);
        Assert.Equal(2, attempts);
        Assert.DoesNotContain(_ctx.JSInterop.Invocations, item => item.Identifier == "claySelectAdvancedSearch.cancel");
        await service.CancelAsync(requestId);
        Assert.Equal("Stale", await service.ApplyAsync(requestId, 2, valid));
    }

    /// <summary>Принятый пустой набор и закрытие второго окна дают разные результаты.</summary>
    [Fact]
    public async Task EmptyMultipleIsAppliedWithoutApplyingCancellationFromAnotherWindow()
    {
        await using var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        var applied = new List<int[]>();
        Task<ClaySelectReturnDecision> Apply(int[] ids, CancellationToken _)
        {
            applied.Add(ids);
            return Task.FromResult(ClaySelectReturnDecision.Accepted);
        }

        const string url = "http://localhost:5030/search";
        var cleared = service.OpenAcknowledgedMultipleIdsAsync(url, Apply);
        var clearingRequest = LastRequestId();
        var cancelled = service.OpenAcknowledgedMultipleIdsAsync(url, Apply);
        await service.CancelAsync(LastRequestId());

        Assert.False(await cancelled);
        Assert.Empty(applied);
        Assert.Equal("Accepted", await service.ApplyAsync(clearingRequest, 1, JsonSerializer.SerializeToElement(Array.Empty<int>())));
        Assert.True(await cleared);
        Assert.Empty(Assert.Single(applied));
    }

    /// <summary>Закрытие дочернего окна отменяет ещё не применённый результат.</summary>
    [Fact]
    public async Task ClosingCancelsAcceptanceInProgress()
    {
        await using var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        var pending = service.OpenAcknowledgedAsync<Employee, int?>("http://localhost:5030/search", async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ClaySelectReturnDecision.Accepted;
        });
        var requestId = LastRequestId();
        var apply = service.ApplyAsync(requestId, 1, JsonSerializer.SerializeToElement(3));
        await service.CancelAsync(requestId);

        Assert.Equal("Stale", await apply);
        Assert.False(await pending);
    }

    /// <summary>Сначала записывается ключ запроса, а отсоединение circuit оставляет исходное окно.</summary>
    [Fact]
    public async Task RecoverableOpen_PersistsRequestBeforeOpeningAndDisposesOnlyBinding()
    {
        await using var service = new ClaySelectAdvancedSearchService(new SynchronizedInvocationRuntime(_ctx.JSInterop.JSRuntime));
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var durable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = service.OpenRecoverableAsync("https://directory.example/search", false, Guid.NewGuid().ToString("N"),
            _ => Task.FromResult(true),
            (id, _) => { started.TrySetResult(id); return durable.Task; },
            (_, _) => throw new InvalidOperationException("Selection dispatched prematurely"));
        var id = await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(_ctx.JSInterop.Invocations["claySelectAdvancedSearch.open"]);
        durable.SetResult();
        await service.DisposeAsync();
        Assert.False(await request);
        Assert.DoesNotContain(_ctx.JSInterop.Invocations, call => call.Identifier == "claySelectAdvancedSearch.cancel");
        Assert.Contains(_ctx.JSInterop.Invocations["claySelectAdvancedSearch.detach"], call => Equals(call.Arguments[0], id));
    }

    /// <summary>Перепривязка проверяет текущий получатель и сохраняет ACK принятой точной ревизии.</summary>
    [Theory]
    [InlineData("107", false)]
    [InlineData("[107,9]", true)]
    [InlineData("[]", true)]
    public async Task ResumeUndispatched_UsesOriginalIdAndDeduplicatesAck(string json, bool multiple)
    {
        await using var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        _ctx.JSInterop.Setup<bool>("claySelectAdvancedSearch.resumeUndispatched", _ => true).SetResult(true);
        var id = Guid.NewGuid().ToString("N");
        var proof = Guid.NewGuid().ToString("N");
        var receiverReady = false;
        var applications = 0;
        var pending = service.ResumeUndispatchedAsync("https://directory.example/search", multiple, id, proof,
            _ => Task.FromResult(receiverReady), (value, _) =>
            {
                Assert.Equal(json, value.GetRawText());
                applications++;
                receiverReady = false;
                return Task.FromResult(ClaySelectReturnDecision.Accepted);
            });
        var bind = Assert.Single(_ctx.JSInterop.Invocations["claySelectAdvancedSearch.resumeUndispatched"]);
        Assert.Equal(id, bind.Arguments[1]);
        Assert.Equal(proof, bind.Arguments[2]);
        Assert.Empty(_ctx.JSInterop.Invocations["claySelectAdvancedSearch.open"]);
        Assert.False(await service.ReadyForSelectionAsync(id, 1));
        receiverReady = true;
        Assert.True(await service.ReadyForSelectionAsync(id, 1));
        using var value = JsonDocument.Parse(json);
        Assert.Equal("Accepted", await service.ApplyAsync(id, 1, value.RootElement));
        Assert.True(await pending);
        Assert.True(await service.ReadyForSelectionAsync(id, 1));
        Assert.False(await service.ReadyForSelectionAsync(id, 2));
        Assert.Equal("Accepted", await service.ApplyAsync(id, 1, value.RootElement));
        Assert.Equal(1, applications);
    }

    /// <summary>Неподтверждённое окно не открывается заново и не создаёт получателя результата.</summary>
    [Fact]
    public async Task ResumeRefused_DoesNotOpenOrApplySelection()
    {
        await using var service = new ClaySelectAdvancedSearchService(_ctx.JSInterop.JSRuntime);
        _ctx.JSInterop.Setup<bool>("claySelectAdvancedSearch.resumeUndispatched", _ => true).SetResult(false);
        var id = Guid.NewGuid().ToString("N");

        Assert.False(await service.ResumeUndispatchedAsync("https://directory.example/search", false, id, Guid.NewGuid().ToString("N"),
            _ => Task.FromResult(true), (_, _) => throw new InvalidOperationException("Unproven receiver")));
        Assert.False(await service.ReadyForSelectionAsync(id, 1));
        Assert.Equal("Stale", await service.ApplyAsync(id, 1, JsonSerializer.SerializeToElement(107)));
        Assert.Empty(_ctx.JSInterop.Invocations["claySelectAdvancedSearch.open"]);
    }

    private string LastRequestId() =>
        (string)_ctx.JSInterop.Invocations.Last(item => item.Identifier == "claySelectAdvancedSearch.open").Arguments[1]!;

    // bUnit регистрирует вызов синхронно. Сам ValueTask ожидается вызывающей стороной вне блокировки.
    private sealed class SynchronizedInvocationRuntime(IJSRuntime inner) : IJSRuntime
    {
        private readonly object _sync = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            lock (_sync)
                return inner.InvokeAsync<TValue>(identifier, args);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            lock (_sync)
                return inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }
    }

    private void AssertCancelInvocation(string requestId)
    {
        Assert.Contains(_ctx.JSInterop.Invocations, item =>
            item.Identifier == "claySelectAdvancedSearch.cancel" && Equals(item.Arguments[0], requestId));
    }
}
