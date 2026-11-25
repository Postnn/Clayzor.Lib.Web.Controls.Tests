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

public sealed class CardWindowOutcomeProposalTests
{
    [Fact]
    public async Task CancelReopensRegistrationWhenWindowRemainsOpen()
    {
        await using var context = new BunitContext();
        var runtime = Configure(context, loads: 2);
        var editor = context.Render<WindowCard>();
        editor.WaitForAssertion(() => Assert.True(editor.Instance.WriteAllowed));

        await editor.Find("#cancel").ClickAsync(new MouseEventArgs());
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(2, runtime.Registrations);

        await editor.Find("#edit").ClickAsync(new MouseEventArgs());
        Assert.True(editor.Instance.WriteAllowed);
        Assert.Contains("next edit", editor.Markup);
        Assert.Equal("window-2", context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last().Arguments[0]);
    }

    [Fact]
    public async Task ReadOnlyReopenKeepsOwnDraftVisibleAndCannotWrite()
    {
        await using var context = new BunitContext();
        Configure(context, loads: 1, existingDraft: true);
        var editor = context.Render<WindowCard>(p => p.Add(c => c.ReadOnly, true));
        editor.WaitForAssertion(() => Assert.Contains("preserved local edit", editor.Markup));
        Assert.Contains("Черновик доступен для просмотра", editor.Markup);
        Assert.False(editor.Instance.WriteAllowed);
        await editor.Find("#edit").ClickAsync(new MouseEventArgs());
        Assert.Contains("preserved local edit", editor.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    [Fact]
    public async Task ParentRefreshFailureDoesNotAbortConfirmedCompletion()
    {
        await using var context = new BunitContext();
        Configure(context, loads: 1);
        var editor = context.Render<WindowCard>();
        editor.WaitForAssertion(() => Assert.True(editor.Instance.WriteAllowed));

        await editor.Find("#notify").ClickAsync(new MouseEventArgs());
        Assert.True(editor.Instance.ReachedNextAction);
        Assert.Contains("Операция подтверждена", editor.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    private static WindowRuntime Configure(BunitContext context, int loads, bool existingDraft = false)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        var runtime = new WindowRuntime(context.JSInterop.JSRuntime, existingDraft);
        context.Services.AddSingleton<IJSRuntime>(runtime);
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, WindowAuthentication>();
        context.Services.AddMudServices();

        DapperColumnMapper.Initialize();
        var employee = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Хост теста")));
        var events = Enumerable.Range(0, loads).SelectMany(_ => new[] { employee, Script.Rows() });
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(events), emulateEquipmentIdentity: false);
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        return runtime;
    }

    public sealed class WindowCard : TerritorialEntityCardBase<string>
    {
        [Parameter] public bool ReadOnly { get; set; }
        internal bool ReachedNextAction { get; private set; }
        internal bool WriteAllowed => CanCommit;
        protected override string FormKey => "window-test";
        protected override string EntityKey => "007";
        protected override string RouteKey => EntityKey;
        protected override bool Editable => !ReadOnly;
        protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;
        protected override Task<string?> LoadCardAsync(CancellationToken routeToken) => Task.FromResult<string?>("current database row");

        private async Task NotifyAsync()
        {
            await RefreshOpenerAsync();
            ReachedNextAction = true;
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "id", "cancel");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, CancelAsync));
            builder.CloseElement();
            builder.OpenElement(3, "button");
            builder.AddAttribute(4, "id", "edit");
            builder.AddAttribute(5, "onclick", EventCallback.Factory.Create(this, () => UpdateDraftAsync("next edit")));
            builder.CloseElement();
            builder.OpenElement(6, "button");
            builder.AddAttribute(7, "id", "notify");
            builder.AddAttribute(8, "onclick", EventCallback.Factory.Create(this, NotifyAsync));
            builder.CloseElement();
            builder.AddContent(9, Draft);
            builder.AddContent(10, Message);
        }
    }

    private sealed class WindowRuntime(IJSRuntime inner, bool existingDraft) : IJSRuntime
    {
        internal int Registrations { get; private set; }

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "clayDraftStore.register")
            {
                var draft = new ClayDraftRestore($"window-{++Registrations}", existingDraft, 0, null, null, existingDraft
                    ? new Dictionary<string, string> { ["main"] = """{"Original":"current database row","Current":"preserved local edit"}""" } : null);
                return ValueTask.FromResult((T)(object)draft);
            }
            if (identifier == "clayModelEquipmentForm.refreshOpener")
                return ValueTask.FromException<T>(new JSException("Тестовая ошибка уведомления"));
            return inner.InvokeAsync<T>(identifier, cancellationToken, args);
        }
    }

    private sealed class WindowAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
