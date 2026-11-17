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

    private void AssertCancelInvocation(string requestId)
    {
        Assert.Contains(_ctx.JSInterop.Invocations, item =>
            item.Identifier == "claySelectAdvancedSearch.cancel" && Equals(item.Arguments[0], requestId));
    }
}
