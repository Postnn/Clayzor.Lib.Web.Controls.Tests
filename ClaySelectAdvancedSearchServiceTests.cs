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

    private string LastRequestId() =>
        (string)_ctx.JSInterop.Invocations.Last(item => item.Identifier == "claySelectAdvancedSearch.open").Arguments[1]!;

    private void AssertCancelInvocation(string requestId)
    {
        Assert.Contains(_ctx.JSInterop.Invocations, item =>
            item.Identifier == "claySelectAdvancedSearch.cancel" && Equals(item.Arguments[0], requestId));
    }
}
