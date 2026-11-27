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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет повтор восстановления общей карточки без переигрывания scope.</summary>
public sealed class TerritorialCardRawRetryTests
{
    /// <summary>Обновление применяет только оставшиеся поля и не заменяет новый typed ввод.</summary>
    [Fact]
    public async Task Refresh_RetriesOnlyPendingMain()
    {
        await using var context = Create(pendingMain: true);
        var page = context.Render<Card>();
        page.WaitForAssertion(() => Assert.Contains("ожидает восстановления", page.Markup));
        await page.InvokeAsync(page.Instance.EditAsync);
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [], ["unrelated-editor"]));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));

        await page.InvokeAsync(page.Instance.RetryAsync);

        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal("main", Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]).Arguments[1]);
        Assert.Contains("newer", page.Markup);
        Assert.Equal("ready", page.Find("output").GetAttribute("data-state"));
    }

    /// <summary>Другой employee не получает raw при повторе.</summary>
    [Fact]
    public async Task Refresh_WhenActorChanged_DoesNotApplyRaw()
    {
        await using var context = Create(pendingMain: true, retryActor: 8);
        var page = context.Render<Card>();
        page.WaitForAssertion(() => Assert.Contains("ожидает восстановления", page.Markup));

        await page.InvokeAsync(page.Instance.RetryAsync);

        Assert.Contains("Не удалось подтвердить сотрудника", page.Markup);
        Assert.Equal("blocked", page.Find("output").GetAttribute("data-state"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
    }

    /// <summary>Чужая область не блокирует готовую операцию, которая принимает только main.</summary>
    [Fact]
    public async Task WriteIgnoresReadinessFromUnacceptedScope()
    {
        await using var context = Create(pendingMain: false);
        var page = context.Render<Card>();
        page.WaitForAssertion(() => Assert.Equal("ready", page.Find("output").GetAttribute("data-state")));
        Assert.True(await page.InvokeAsync(page.Instance.SaveAsync));
    }

    private static BunitContext Create(bool pendingMain, int retryActor = 4)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        DapperColumnMapper.Initialize();
        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Fixture"))), Script.Rows(),
            Script.Rows(Script.Row(("КодСотрудника", retryActor), ("Сотрудник", "Fixture"))), Script.Rows(),
        ]);
        var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        context.Services.AddSingleton(_ => new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection));
        context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
        context.Services.AddSingleton<ClayDraftService>();
        context.Services.AddMudServices();
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("card", true, 3, null,
                new Dictionary<string, string> { ["main"] = """{"Original":{"Value":"base"},"Current":{"Value":"restored"}}""" }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], pendingMain ? ["data:fixture.note:0"] : []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, pendingMain ? ["main"] : ["unrelated-editor"], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, 3, 3));
        return context;
    }

    public sealed class Card : TerritorialEntityCardBase<CardState>
    {
        protected override string RouteKey => "id";
        protected override string EntityKey => "id";
        protected override string FormKey => "test-card-raw";
        protected override bool Editable => true;
        protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;
        protected override Task<CardState?> LoadCardAsync(CancellationToken routeToken) => Task.FromResult<CardState?>(new("base"));

        internal Task EditAsync() => UpdateDraftAsync(new("newer"));
        internal Task RetryAsync() => RefreshAsync();
        internal Task<bool> SaveAsync() => ExecuteWriteAsync("test-card.write", _ => Task.CompletedTask);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "output");
            builder.AddAttribute(1, "data-state", CanCommit ? "ready" : "blocked");
            builder.AddContent(2, Draft?.Value);
            builder.AddContent(3, Message);
            builder.CloseElement();
        }
    }

    public sealed record CardState(string Value);

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }
}
