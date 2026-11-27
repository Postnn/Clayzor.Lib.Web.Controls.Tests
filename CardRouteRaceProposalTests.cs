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
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class CardRouteRaceProposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateRouteACannotReplaceOrReceiveInputFromRouteB(bool delayRegistration)
    {
        await using var context = new BunitContext();
        ConfigureContext(context);
        var routeARegistration = context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", i => Equals(i.Arguments[1], "A"));
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", i => Equals(i.Arguments[1], "B"))
            .SetResult(new ClayDraftRestore("register-B", false, 0, null, null));

        var card = context.Render<RouteCard>(p => p.Add(c => c.Key, "A").Add(c => c.DelayRead, !delayRegistration));
        if (delayRegistration)
            card.WaitForAssertion(() => Assert.Single(routeARegistration.Invocations));
        else
            await card.Instance.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        card.Render(p => p.Add(c => c.Key, "B").Add(c => c.DelayRead, false));
        card.WaitForAssertion(() => Assert.Equal("B", card.Find("output").TextContent));

        if (delayRegistration)
            routeARegistration.SetResult(new ClayDraftRestore("register-A", false, 0, null, null));
        else
        {
            routeARegistration.SetResult(new ClayDraftRestore("unexpected-A", false, 0, null, null));
            card.Instance.ReleaseRead.TrySetResult();
        }

        await card.Instance.FirstRenderFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await card.Find("button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("B-edited", card.Find("output").TextContent);
        Assert.Equal("register-B", context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last().Arguments[0]);
        if (delayRegistration)
            Assert.Contains(context.JSInterop.Invocations["clayDraftStore.unregister"], i => Equals(i.Arguments[0], "register-A"));
        else
            Assert.Empty(routeARegistration.Invocations);
    }

    [Fact]
    public async Task LateRawApplyCannotBlockTheNextRoute()
    {
        await using var context = new BunitContext();
        ConfigureContext(context);
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", i => Equals(i.Arguments[1], "A"))
            .SetResult(new ClayDraftRestore("register-A", true, 0, null,
                new Dictionary<string, string> { ["main"] = """{"Original":{"Value":"A"},"Current":{"Value":"A"}}""" }));
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", i => Equals(i.Arguments[1], "B"))
            .SetResult(new ClayDraftRestore("register-B", false, 0, null, null));
        var raw = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", i => Equals(i.Arguments[0], "register-A"));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", i => Equals(i.Arguments[0], "register-A"))
            .SetResult(new ClayDraftRestoreReadiness(false, ["main"], []));

        var card = context.Render<RouteCard>(p => p.Add(c => c.Key, "A"));
        card.WaitForAssertion(() => Assert.Single(raw.Invocations));
        card.Render(p => p.Add(c => c.Key, "B"));
        card.WaitForAssertion(() => Assert.Equal("ready", card.Find("output").GetAttribute("data-status")));

        raw.SetResult(new ClayDraftRawApplyResult([], ["pending"]));
        await card.Instance.FirstRawRenderFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("B", card.Find("output").TextContent);
        Assert.Equal("ready", card.Find("output").GetAttribute("data-status"));
        Assert.DoesNotContain("восстанов", card.Markup);
        var registrations = context.JSInterop.Invocations["clayDraftStore.register"];
        Assert.NotEqual(registrations[0].Arguments[4], registrations[1].Arguments[4]);
    }

    [Fact]
    public async Task ReturningToAWaitsForItsPreviousRegistration()
    {
        await using var context = new BunitContext();
        ConfigureContext(context, routeLoads: 3);
        var registrationA = context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", i => Equals(i.Arguments[1], "A"));
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", i => Equals(i.Arguments[1], "B"))
            .SetResult(new ClayDraftRestore("register-B", false, 0, null, null));

        var card = context.Render<RouteCard>(p => p.Add(c => c.Key, "A"));
        card.WaitForAssertion(() => Assert.Single(registrationA.Invocations));
        card.Render(p => p.Add(c => c.Key, "B"));
        card.WaitForAssertion(() => Assert.Equal("B", card.Find("output").TextContent));
        card.Render(p => p.Add(c => c.Key, "A"));
        card.WaitForAssertion(() => Assert.Equal("loading", card.Find("output").TextContent));
        Assert.Single(registrationA.Invocations);

        registrationA.SetResult(new ClayDraftRestore("register-A", false, 0, null, null));
        card.WaitForAssertion(() => Assert.Equal("ready", card.Find("output").GetAttribute("data-status")));
        Assert.Equal("A", card.Find("output").TextContent);
        Assert.Equal(2, registrationA.Invocations.Count);
        var registrations = registrationA.Invocations.ToArray();
        Assert.NotEqual(registrations[0].Arguments[4], registrations[1].Arguments[4]);
        Assert.Contains(context.JSInterop.Invocations["clayDraftStore.unregister"], i => Equals(i.Arguments[0], "register-A"));
    }


    private static void ConfigureContext(BunitContext context, int routeLoads = 2)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        DapperColumnMapper.Initialize();
        var employee = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Хост теста")));
        var events = Enumerable.Range(0, routeLoads).SelectMany(_ => new[] { employee, Script.Rows() });
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(events),
            emulateEquipmentIdentity: false);
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        context.Services.AddSingleton<AuthenticationStateProvider, RouteAuthentication>();
        context.Services.AddSingleton<ClayDraftService>();
        context.Services.AddMudServices();
    }


    public sealed class RouteCard : TerritorialEntityCardBase<RouteState>
    {
        [Parameter] public string Key { get; set; } = "";
        [Parameter] public bool DelayRead { get; set; }
        internal TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstRenderFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstRawRenderFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override string RouteKey => Key;
        protected override string EntityKey => Key;
        protected override string FormKey => "route-race-test";
        protected override bool Editable => true;
        protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;

        protected override async Task<RouteState?> LoadCardAsync(CancellationToken routeToken)
        {
            var key = Key;
            if (DelayRead)
            {
                FirstRead.TrySetResult();
                await ReleaseRead.Task;
            }
            return new RouteState(key);
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            var key = Key;
            await base.OnAfterRenderAsync(firstRender);
            if (firstRender) FirstRenderFinished.TrySetResult();
            else if (key == "A") FirstRawRenderFinished.TrySetResult();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "output");
            builder.AddAttribute(1, "data-status", CanCommit ? "ready" : "blocked");
            builder.AddContent(2, Loading ? "loading" : Draft?.Value);
            builder.AddContent(3, Message);
            builder.CloseElement();
            builder.OpenElement(4, "button");
            builder.AddAttribute(5, "onclick", EventCallback.Factory.Create(this, () => UpdateDraftAsync(new RouteState(Draft?.Value + "-edited"))));
            builder.CloseElement();
        }
    }

    public sealed record RouteState(string Value);

    private sealed class RouteAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
