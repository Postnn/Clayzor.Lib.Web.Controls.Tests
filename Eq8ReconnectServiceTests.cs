using System.Reflection;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки reconnect reload policy без 30-секундного ожидания.</summary>
public sealed class Eq8ReconnectServiceTests
{
    /// <summary>Health success и canReload=true сохраняют прежний forceLoad.</summary>
    [Fact]
    public async Task HealthyAndReloadable_ForcesReload()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1)]));
        var nav = new ReloadNavigationManager();
        using var service = CreateService(connection, nav, new ReloadJsRuntime(true));

        await InvokeLoop(service);

        Assert.Equal(1, nav.ForceLoadCount);
        Assert.NotEqual(ReconnectState.Failed, service.State);
        Assert.Equal(string.Empty, service.LastError);
    }

    /// <summary>Неподтверждённый draft запрещает reload и объясняет отказ.</summary>
    [Fact]
    public async Task HealthyButDraftNotReloadable_DoesNotReload()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1)]));
        var nav = new ReloadNavigationManager();
        using var service = CreateService(connection, nav, new ReloadJsRuntime(false));

        await InvokeLoop(service);

        Assert.Equal(0, nav.ForceLoadCount);
        Assert.Equal(ReconnectState.Failed, service.State);
        Assert.Contains("черновик ещё не сохранён", service.LastError);
    }

    /// <summary>JS failure fail-closed: reload отсутствует, пользователю остаётся LastError.</summary>
    [Fact]
    public async Task HealthyButCanReloadThrows_DoesNotReloadAndKeepsError()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1)]));
        var nav = new ReloadNavigationManager();
        var js = new ReloadJsRuntime(true) { Failure = new JSException("store failed") };
        using var service = CreateService(connection, nav, js);

        await InvokeLoop(service);

        Assert.Equal(0, nav.ForceLoadCount);
        Assert.Equal(ReconnectState.Failed, service.State);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
        Assert.Contains("Автоматическая перезагрузка отменена", service.LastError);
    }

    /// <summary>Прежний unsuccessful health-check остаётся false без reload и исключения наружу.</summary>
    [Fact]
    public async Task UnsuccessfulHealthCheck_ReturnsFalse()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var nav = new ReloadNavigationManager();
        using var service = CreateService(connection, nav, new ReloadJsRuntime(true));

        var healthy = await InvokeHealthCheck(service);

        Assert.False(healthy);
        Assert.Equal(0, nav.ForceLoadCount);
    }

    private static ClayDbReconnectService CreateService(
        ScriptedConnection connection,
        NavigationManager nav,
        IJSRuntime js) =>
        new(new ClayErrorService(),
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), nav, js);

    private static Task InvokeLoop(ClayDbReconnectService service) =>
        (Task)typeof(ClayDbReconnectService)
            .GetMethod("ReconnectLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [CancellationToken.None])!;

    private static Task<bool> InvokeHealthCheck(ClayDbReconnectService service) =>
        (Task<bool>)typeof(ClayDbReconnectService)
            .GetMethod("TryHealthCheckAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [CancellationToken.None])!;

    private sealed class ReloadJsRuntime(bool canReload) : IJSRuntime
    {
        public Exception? Failure { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Invoke<TValue>();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => Invoke<TValue>();

        private ValueTask<TValue> Invoke<TValue>()
        {
            if (Failure is not null)
                return ValueTask.FromException<TValue>(Failure);
            return new ValueTask<TValue>((TValue)(object)canReload);
        }
    }

    private sealed class ReloadNavigationManager : NavigationManager
    {
        public int ForceLoadCount { get; private set; }

        public ReloadNavigationManager() => Initialize("http://localhost/equipment/7", "http://localhost/equipment/7");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            if (forceLoad) ForceLoadCount++;
            Uri = uri;
        }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            if (options.ForceLoad) ForceLoadCount++;
            Uri = uri;
        }
    }
}
