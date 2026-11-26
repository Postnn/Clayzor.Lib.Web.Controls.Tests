using System.Security.Claims;
using Bunit;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки ключей, прав и жизненного цикла presence без БД.</summary>
public sealed class ClayPresenceTests
{
    private static readonly ClayPresenceOptions Options = new()
    {
        HeartbeatInterval = TimeSpan.FromSeconds(10),
        LeaseLifetime = TimeSpan.FromSeconds(30)
    };

    private static readonly ClayPresenceKey Attribute = ClayPresenceKey.Create("test.inventory", "attribute", 5, "Тел:VoIP");
    private static readonly ClayPresenceAccess First = new(101, "Первый", ClayPresenceActivity.Editing);
    private static readonly ClayPresenceAccess Second = new(102, "Второй", ClayPresenceActivity.Editing);

    [Fact]
    public void CompositeIdentitySeparatesTypeScopeAndTypedSegments()
    {
        var original = ClayPresenceKey.Create("sql.database", "attribute", 17, "a:b");
        var alias = ClayPresenceKey.Create("sql.database", "attribute", 17L, "a:b");

        Assert.Equal(original, alias);
        Assert.NotEqual(original, ClayPresenceKey.Create("sql.other", "attribute", 17, "a:b"));
        Assert.NotEqual(original, ClayPresenceKey.Create("sql.database", "pool", 17, "a:b"));
        Assert.NotEqual(original, ClayPresenceKey.Create("sql.database", "attribute", "17", "a:b"));
        Assert.NotEqual(original, ClayPresenceKey.Create("sql.database", "attribute", 17, "a", "b"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClayPresenceKey.Create("scope", "attribute", 0));
    }

    [Fact]
    public async Task MultipleTabsAggregateAndOwnTabsAreExcluded()
    {
        var clock = new ManualClock();
        var store = new ClayProcessPresenceRegistry(clock, Options);
        clock.Advance(Options.LeaseLifetime);
        var viewing = First with { Activity = ClayPresenceActivity.Viewing };
        var firstView = await Heartbeat(store, viewing);
        var firstEdit = await Heartbeat(store, First);
        await store.HeartbeatAsync(ClayPresenceKey.Create("test.inventory", "pool", 5), new(103, "Другой", ClayPresenceActivity.Editing), null, default);

        var second = await Heartbeat(store, Second);
        Assert.Equal(First, Assert.Single(second.Participants!));
        Assert.Equal(Second, Assert.Single((await Heartbeat(store, First, firstEdit.Lease)).Participants!));

        await store.LeaveAsync(firstEdit.Lease);
        Assert.Equal(viewing, Assert.Single((await Heartbeat(store, Second, second.Lease)).Participants!));

        await store.LeaveAsync(firstView.Lease);
        Assert.Empty((await Heartbeat(store, Second, second.Lease)).Participants!);
    }

    [Fact]
    public async Task WarmupAndExpiryNeverResurrectOrRemoveNewGeneration()
    {
        var clock = new ManualClock();
        var store = new ClayProcessPresenceRegistry(clock, Options);
        var old = await Heartbeat(store, First);
        Assert.Null(old.Participants);
        clock.Advance(Options.LeaseLifetime);
        Assert.Null(await store.HeartbeatAsync(Attribute, First, old.Lease, default));

        var replacement = await Heartbeat(store, First);
        Assert.Empty(replacement.Participants!);
        Assert.NotEqual(old.Lease, replacement.Lease);
        await store.LeaveAsync(old.Lease);
        Assert.Null(await store.HeartbeatAsync(Attribute, First, old.Lease, default));
        Assert.Equal(First, Assert.Single((await Heartbeat(store, Second)).Participants!));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            store.HeartbeatAsync(Attribute, First, replacement.Lease, cancellation.Token));
        clock.Advance(Options.LeaseLifetime);
        Assert.Empty((await Heartbeat(store, Second)).Participants!);
    }

    [Fact]
    public async Task ServerAuthorizationExcludesAnonymousAndRemovesRevokedOrReplacedIdentity()
    {
        var clock = new ManualClock();
        var store = new ClayProcessPresenceRegistry(clock, Options);
        clock.Advance(Options.LeaseLifetime);
        var auth = new AuthenticationStub();
        var poll = new ClayPresencePoll(auth, store);
        var requests = 0;
        var allowed = true;
        var target = new ClayPresenceTarget(Attribute, (principal, _) =>
        {
            requests++;
            var access = principal.Identity!.Name == "domain\\first" ? First : Second;
            return ValueTask.FromResult(allowed ? access : null);
        });

        Assert.Null(await poll.UpdateAsync(target, default));
        Assert.Equal(0, requests);
        auth.Set("domain\\first");
        Assert.NotNull(await poll.UpdateAsync(target, default));
        var otherTab = await Heartbeat(store, Second);
        Assert.Equal(First, Assert.Single(otherTab.Participants!));
        await store.LeaveAsync(otherTab.Lease);

        auth.Set("domain\\second");
        Assert.Empty((await poll.UpdateAsync(target, default))!.Participants!);
        Assert.Equal(Second, Assert.Single((await Heartbeat(store, First)).Participants!));

        allowed = false;
        Assert.Null(await poll.UpdateAsync(target, default));
        Assert.Empty((await Heartbeat(store, First)).Participants!);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task PanelRechecksOnReconnectAndRejectsPreviousEntityResponse()
    {
        using var context = new BunitContext();
        var clock = new ManualClock();
        var auth = new AuthenticationStub();
        auth.Set("domain\\first");
        context.Services.AddSingleton<AuthenticationStateProvider>(auth);
        context.Services.AddSingleton<TimeProvider>(clock);
        context.Services.AddClayEntityPresence();
        var store = context.Services.GetRequiredService<IClayPresenceRegistry>();
        var circuit = context.Services.GetServices<CircuitHandler>().OfType<ClayPresenceCircuit>().Single();
        clock.Advance(TimeSpan.FromSeconds(45));
        await Heartbeat(store, Second with { DisplayName = "<old entity>" });
        var oldAuthorization = new TaskCompletionSource<ClayPresenceAccess?>();
        var old = new ClayPresenceTarget(Attribute, (_, _) => new(oldAuthorization.Task));
        var newKey = ClayPresenceKey.Create("test.inventory", "attribute", 7, "VoIP");
        await store.HeartbeatAsync(newKey, Second with { DisplayName = "<trusted name>" }, null, default);
        var checks = 0;
        var current = new ClayPresenceTarget(newKey, (_, _) =>
        {
            checks++;
            return ValueTask.FromResult<ClayPresenceAccess?>(First);
        });

        await circuit.OnConnectionUpAsync(null!, default);
        var cut = context.Render<ClayPresencePanel>(parameters => parameters.Add(panel => panel.Target, old));
        Assert.Contains("Подключение", cut.Markup);
        cut.Render(parameters => parameters.Add(panel => panel.Target, current));
        cut.WaitForAssertion(() => Assert.Contains("<trusted name>", cut.Find("li").TextContent));
        Assert.Contains("&lt;trusted name&gt;", cut.Markup);

        await cut.InvokeAsync(() => oldAuthorization.SetResult(First));
        Assert.DoesNotContain("old entity", cut.Markup);
        Assert.Empty((await Heartbeat(store, Second)).Participants!);

        await cut.InvokeAsync(() => circuit.OnConnectionDownAsync(null!, default));
        Assert.Contains("Не удалось получить сведения", cut.Markup);
        Assert.DoesNotContain("trusted name", cut.Markup);
        await cut.InvokeAsync(() => circuit.OnConnectionUpAsync(null!, default));
        cut.WaitForAssertion(() => Assert.Contains("<trusted name>", cut.Find("li").TextContent));
        Assert.Equal(2, checks);
    }

    private static async Task<ClayPresenceSnapshot> Heartbeat(
        IClayPresenceRegistry store, ClayPresenceAccess access, Guid? lease = null) =>
        (await store.HeartbeatAsync(Attribute, access, lease, default))!;

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class AuthenticationStub : AuthenticationStateProvider
    {
        private ClaimsPrincipal _principal = new(new ClaimsIdentity());

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_principal));

        internal void Set(string name)
        {
            _principal = new(new ClaimsIdentity([new(ClaimTypes.Name, name)], "Windows"));
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }
}
