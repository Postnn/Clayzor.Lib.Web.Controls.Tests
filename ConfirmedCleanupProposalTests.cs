using System.Security.Claims;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class ConfirmedCleanupProposalTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RefreshRetriesOnlyStorageThenCompletesPostSaveOnce(int failures)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("cleanup-test", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"test-attempt","kind":"update","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var runtime = new FailingCleanupRuntime(context.JSInterop.JSRuntime, failures);
        context.Services.AddSingleton<IJSRuntime>(runtime);
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, CleanupAuthentication>();
        context.Services.AddMudServices();

        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Хост теста"))), Script.Rows()
        ]), emulateEquipmentIdentity: false);
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var editor = context.Render<CleanupCard>();
        editor.WaitForAssertion(() => Assert.False(editor.Find("#save").HasAttribute("disabled")));
        await editor.Find("#save").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, editor.Instance.DatabaseWrites);
        Assert.Equal(0, editor.Instance.CompletionCount);
        Assert.True(editor.Find("#save").HasAttribute("disabled"));
        Assert.False(editor.Find("#refresh").HasAttribute("disabled"));
        Assert.Contains("База подтвердила", editor.Markup);
        await editor.InvokeAsync(editor.Instance.SaveAsync);
        Assert.Equal(1, editor.Instance.DatabaseWrites);

        for (var i = 0; i < failures; i++)
            await editor.Find("#refresh").ClickAsync(new MouseEventArgs());

        Assert.Equal(failures + 1, runtime.CleanupCalls);
        Assert.Equal(1, editor.Instance.DatabaseWrites);
        Assert.Equal(1, editor.Instance.CompletionCount);
        Assert.False(editor.Find("#save").HasAttribute("disabled"));
        Assert.DoesNotContain("База подтвердила", editor.Markup);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
    }

    public sealed class CleanupCard : TerritorialEntityCardBase<string>
    {
        internal int DatabaseWrites { get; private set; }
        internal int CompletionCount { get; private set; }

        protected override string FormKey => "cleanup-probe";
        protected override string EntityKey => "saved";
        protected override string RouteKey => EntityKey;
        protected override bool Editable => true;
        protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;
        protected override Task<string?> LoadCardAsync(CancellationToken routeToken) => Task.FromResult<string?>("original");

        internal async Task SaveAsync()
        {
            if (!await ExecuteWriteAsync("update", _ =>
            {
                DatabaseWrites++;
                return Task.CompletedTask;
            })) return;
            Accept("confirmed");
            await AfterDraftCleanupAsync(() =>
            {
                CompletionCount++;
                return Task.CompletedTask;
            });
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "id", "save");
            builder.AddAttribute(2, "disabled", !CanCommit);
            builder.AddAttribute(3, "onclick", EventCallback.Factory.Create(this, SaveAsync));
            builder.CloseElement();
            builder.OpenElement(4, "button");
            builder.AddAttribute(5, "id", "refresh");
            builder.AddAttribute(6, "disabled", !CanRefresh);
            builder.AddAttribute(7, "onclick", EventCallback.Factory.Create(this, RefreshAsync));
            builder.CloseElement();
            builder.AddContent(8, Message);
        }
    }

    private sealed class FailingCleanupRuntime(IJSRuntime inner, int failures) : IJSRuntime
    {
        internal int CleanupCalls { get; private set; }

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "clayDraftStore.commitOperation" && ++CleanupCalls <= failures)
                return ValueTask.FromException<T>(new JSException("Тестовая ошибка IndexedDB"));
            return inner.InvokeAsync<T>(identifier, cancellationToken, args);
        }
    }

    private sealed class CleanupAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
