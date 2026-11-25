using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class StorageRetryProposalTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshRepairsStorageBeforeAnyDispatch(bool failRegistration)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("storage-test", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"new-attempt","kind":"update","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var runtime = new FailOnceRuntime(context.JSInterop.JSRuntime, failRegistration
            ? "clayDraftStore.register" : "clayDraftStore.beginOperationIntent");
        context.Services.AddSingleton<IJSRuntime>(runtime);
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, StorageAuthentication>();
        context.Services.AddMudServices();

        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Хост теста"))), Script.Rows()
        ]), emulateEquipmentIdentity: false);
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var editor = context.Render<ConfirmedCleanupProposalTests.CleanupCard>();
        if (!failRegistration)
        {
            editor.WaitForAssertion(() => Assert.False(editor.Find("#save").HasAttribute("disabled")));
            await editor.Find("#save").ClickAsync(new MouseEventArgs());
            Assert.Contains("Команда не отправлена", editor.Markup);
        }

        editor.WaitForAssertion(() => Assert.Contains("хранилищ", editor.Markup.ToLowerInvariant()));
        Assert.True(editor.Find("#save").HasAttribute("disabled"));
        Assert.False(editor.Find("#refresh").HasAttribute("disabled"));
        Assert.Equal(0, editor.Instance.DatabaseWrites);

        await editor.Find("#refresh").ClickAsync(new MouseEventArgs());
        Assert.False(editor.Find("#save").HasAttribute("disabled"));
        Assert.Equal(0, editor.Instance.DatabaseWrites);
        if (failRegistration)
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.retryStorage"]);
        else
        {
            var refresh = Assert.Single(context.JSInterop.Invocations["clayDraftStore.retryStorage"]);
            Assert.NotNull(runtime.UnsentAttempt);
            Assert.Equal(runtime.UnsentAttempt, refresh.Arguments[1]);
        }

        await editor.Find("#save").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, editor.Instance.DatabaseWrites);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    private sealed class FailOnceRuntime(IJSRuntime inner, string failingMethod) : IJSRuntime
    {
        private bool _failed;
        internal string? UnsentAttempt { get; private set; }

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == failingMethod && !_failed)
            {
                _failed = true;
                if (identifier == "clayDraftStore.beginOperationIntent")
                {
                    using var serialized = JsonDocument.Parse((string)args![1]!);
                    UnsentAttempt = serialized.RootElement.GetProperty("attemptId").GetString();
                }
                return ValueTask.FromException<T>(new JSException("Тестовая ошибка IndexedDB"));
            }
            return inner.InvokeAsync<T>(identifier, cancellationToken, args);
        }
    }

    private sealed class StorageAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
