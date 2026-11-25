using System.Net;
using System.Net.NetworkInformation;
using System.Security.Claims;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.PingHost;
using Kesco.App.Web.Inventory.Services.PingDiagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет область доступа, совместимый JSON и жизненный цикл PingHost.</summary>
public sealed class PingHostTests
{
    /// <summary>URL и неразрешённые цели отклоняются до I/O.</summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("http://localhost/probe", 0)]
    [InlineData("localhost/path", 0)]
    [InlineData("unlisted.test", 1)]
    [InlineData("192.0.2.10", 1)]
    public async Task InvalidOrUnauthorizedHostNeverTouchesNetwork(string host, int failure)
    {
        var transport = new Probe();
        var service = CreateService(transport, new ManualClock());
        var error = await Assert.ThrowsAsync<PingDiagnosticException>(() => service.ResolveAsync(host, default));
        Assert.Equal((PingFailureKind)failure, error.Kind);
        Assert.Equal(0, transport.Resolutions);
        Assert.Equal(0, transport.Sends);
    }

    /// <summary>Отклонённый первый адрес DNS не приводит к перебору или отправке.</summary>
    [Fact]
    public async Task FirstResolvedAddressMustBeAllowed()
    {
        var transport = new Probe { Addresses = [IPAddress.Parse("192.0.2.10"), IPAddress.Loopback] };
        var service = CreateService(transport, new ManualClock());
        var error = await Assert.ThrowsAsync<PingDiagnosticException>(() => service.ResolveAsync("LOCALHOST", default));
        Assert.Equal(PingFailureKind.Forbidden, error.Kind);
        Assert.Equal(0, transport.Sends);
    }

    /// <summary>DNS завершается по отменяемому таймауту. Пустой ответ — отдельная ошибка разрешения.</summary>
    [Fact]
    public async Task DnsDeadlineCancelsTransport()
    {
        var clock = new ManualClock();
        var transport = new Probe { HoldDns = true };
        var service = CreateService(transport, clock);
        var attempt = service.ResolveAsync("localhost", default);
        await transport.DnsEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<PingDiagnosticException>(() => attempt);
        Assert.Equal(PingFailureKind.Resolution, error.Kind);
        Assert.True(transport.DnsCancelled);
        transport.HoldDns = false;
        transport.Addresses = [];
        Assert.Equal(PingFailureKind.Resolution, (await Assert.ThrowsAsync<PingDiagnosticException>(
            () => service.ResolveAsync("localhost", default))).Kind);
    }

    /// <summary>IPv6 с RTT 0 и без TTL форматируется без HTML. Ошибочный ICMP-статус остаётся результатом попытки.</summary>
    [Fact]
    public async Task FormatsZeroRoundtripAndContinuesAfterFailedStatus()
    {
        var clock = new ManualClock();
        var transport = new Probe { Reply = new(IPStatus.TimedOut, null, 0, 0, null) };
        var service = CreateService(transport, clock);
        var target = await service.ResolveAsync("::1", default);
        var attempt = service.AttemptAsync(target, default);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(0, transport.Sends);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False((await attempt).Success);

        transport.Reply = new(IPStatus.Success, IPAddress.IPv6Loopback, 32, 0, null);
        attempt = service.AttemptAsync(target, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        var line = await attempt;
        Assert.True(line.Success);
        Assert.Equal("Ответ от ::1: число байт=32 время<1мс", line.Text);
    }

    /// <summary>Повторный callback Start во время DNS не создаёт второй запуск.</summary>
    [Fact]
    public async Task StopDuringDnsAndRestart()
    {
        var transport = new Probe { HoldDns = true };
        await using var context = Configure(transport, new ManualClock());
        var page = context.Render<PingHostPage>();
        await transport.DnsEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var start = page.FindComponents<ClayToolbarButton>().Single(x => x.Instance.Options?.Text == "Запустить");
        await page.InvokeAsync(() => start.Instance.OnClick.InvokeAsync());
        Assert.Equal(1, transport.Resolutions);

        await Button(page, "Остановить").ClickAsync(new MouseEventArgs());
        Assert.True(transport.DnsCancelled);
        Assert.Contains("Проверка остановлена.", page.Markup);
        await Button(page, "Запустить").ClickAsync(new MouseEventArgs());
        await EventuallyAsync(() => transport.Resolutions == 2);
        await Button(page, "Остановить").ClickAsync(new MouseEventArgs());
        Assert.Equal(0, transport.Sends);
    }

    /// <summary>Запоздалый ответ игнорируется после запроса Stop.</summary>
    [Fact]
    public async Task LateReplyCannotReappearInTheJournal()
    {
        var clock = new ManualClock();
        var transport = new Probe { PendingReply = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var context = Configure(transport, clock);
        var page = context.Render<PingHostPage>();
        try
        {
            await EventuallyAsync(() => clock.HasTimers);
            clock.Advance(TimeSpan.FromSeconds(1));
            await transport.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stop = Button(page, "Остановить").ClickAsync(new MouseEventArgs());
            await EventuallyAsync(() => transport.SendToken.IsCancellationRequested);
            transport.PendingReply!.TrySetResult(new NetworkPingReply(IPStatus.Success, IPAddress.Loopback, 32, 0, 128));
            await stop;
            Assert.Empty(page.Find("[role=log]").Children);
            Assert.Contains("Проверка остановлена.", page.Markup);
        }
        finally { transport.PendingReply!.TrySetResult(null); }
    }

    /// <summary>При восстановлении прежнего журнала проверка ожидает явного запуска.</summary>
    [Fact]
    public async Task RestoredJournalNeverAutoStarts()
    {
        var transport = new Probe();
        const string journal = """{"Heading":"Сохранённый адрес","Status":"Проверка выполняется","Error":false,"Running":true,"Lines":[{"Success":true,"Text":"время<1мс"}]}""";
        await using var context = Configure(transport, new ManualClock(), journal);
        var page = context.Render<PingHostPage>();
        page.WaitForAssertion(() => Assert.Contains("Предыдущая проверка прервана", page.Markup));
        Assert.Equal("время<1мс", page.Find("[role=log]").TextContent.Trim());
        Assert.Equal(0, transport.Resolutions);
        Assert.Equal(0, transport.Sends);
    }

    /// <summary>Разрыв circuit прекращает диагностический запуск.</summary>
    [Fact]
    public async Task CircuitDisconnectCancelsDns()
    {
        var transport = new Probe { HoldDns = true };
        await using var context = Configure(transport, new ManualClock());
        var page = context.Render<PingHostPage>();
        await transport.DnsEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await context.Services.GetRequiredService<PingCircuitLifetime>().OnConnectionDownAsync(null!, default);
        page.WaitForAssertion(() => Assert.Contains("прервана после потери связи", page.Markup));
        Assert.True(transport.DnsCancelled);
    }

    /// <summary>После 33 результатов в журнале остаются последние 31.</summary>
    [Fact]
    public async Task JournalKeepsTheLatest31Rows()
    {
        var clock = new ManualClock();
        var transport = new Probe();
        await using var context = Configure(transport, clock);
        var page = context.Render<PingHostPage>();
        for (var i = 1; i <= 33; i++)
        {
            await EventuallyAsync(() => clock.HasTimers);
            clock.Advance(TimeSpan.FromSeconds(1));
            var count = i;
            page.WaitForAssertion(() => Assert.Contains($"время={count}мс TTL=128", page.Find("[role=log]").TextContent));
        }
        await Button(page, "Остановить").ClickAsync(new MouseEventArgs());
        var lines = page.Find("[role=log]").Children;
        Assert.Equal(31, lines.Length);
        Assert.Contains("время=3мс", lines.First().TextContent);
    }

    /// <summary>Совместимый результат сохраняет статусы и ключ деталей.</summary>
    [Fact]
    public void LegacyThresholds()
    {
        var success = new PingLine(true, "ответ");
        var fail = new PingLine(false, "таймаут");
        Assert.Equal("error", LegacyPingResult.FromAttempts([]).Result);
        Assert.Equal("error", LegacyPingResult.FromAttempts([success, fail]).Result);
        Assert.Equal("warning", LegacyPingResult.FromAttempts([success, success, fail]).Result);
        var result = LegacyPingResult.FromAttempts([success]);
        Assert.Equal("success", result.Result);
        Assert.Equal("success", result.Details["1"]);
    }

    /// <summary>Анонимный и cross-site вызовы отклоняются без базы и сети.</summary>
    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 403)]
    public async Task EndpointRejectsAnonymousAndCrossSite(bool crossSite, int status)
    {
        var http = new DefaultHttpContext();
        if (crossSite) http.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        var result = await NetworkPingEndpoint.HandleAsync(http, null!, null!);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    /// <summary>Новый host сначала останавливает старое DNS-ожидание и получает чистый журнал.</summary>
    [Fact]
    public async Task RouteChangeStopsPriorHostBeforeStartingTheNewOne()
    {
        var clock = new ManualClock();
        var transport = new Probe { HoldDns = true };
        await using var context = Configure(transport, clock);
        var page = context.Render<PingHostPage>();
        await transport.DnsEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await page.InvokeAsync(() => context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/Equipment/PingHost?host=127.0.0.1"));
        page.WaitForAssertion(() => Assert.Contains("Обмен пакетами с 127.0.0.1", page.Markup));
        Assert.True(transport.DnsCancelled);
        await EventuallyAsync(() => clock.HasTimers);
        clock.Advance(TimeSpan.FromSeconds(1));
        page.WaitForAssertion(() => Assert.Contains("Ответ от 127.0.0.1", page.Find("[role=log]").TextContent));
        await Button(page, "Остановить").ClickAsync(new MouseEventArgs());
        Assert.Single(page.Find("[role=log]").Children);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<PingHostPage> page, string label) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == label);

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private static PingDiagnosticService CreateService(Probe transport, ManualClock time) =>
        new(transport, new Settings(), time);

    private static BunitContext Configure(Probe transport, ManualClock time, string? saved = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("ping-test", saved is not null, 0, null, null,
                saved is null ? null : new Dictionary<string, string> { ["journal"] = saved }));
        DapperColumnMapper.Initialize();
        context.Services.AddScoped(_ => new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => new ScriptedConnection(new Queue<ScriptEntry>(), emulateEquipmentIdentity: true)));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<PingCircuitLifetime>();
        context.Services.AddSingleton(CreateService(transport, time));
        context.Services.AddSingleton<AuthenticationStateProvider, Auth>();
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/Equipment/PingHost?host=localhost");
        return context;
    }

    private sealed class Auth : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class Settings : IOptionsMonitor<PingDiagnosticOptions>
    {
        public PingDiagnosticOptions CurrentValue { get; } = new() { AllowedHosts = ["localhost"], AllowedAddresses = ["127.0.0.1", "::1"] };
        public PingDiagnosticOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PingDiagnosticOptions, string?> listener) => null;
    }

    private sealed class Probe : INetworkPingTransport
    {
        public IPAddress[] Addresses { get; set; } = [IPAddress.Loopback];
        public bool HoldDns { get; set; }
        public bool DnsCancelled { get; private set; }
        public int Resolutions;
        public int Sends;
        public NetworkPingReply? Reply { get; set; }
        public TaskCompletionSource<NetworkPingReply?>? PendingReply { get; init; }
        public TaskCompletionSource DnsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken SendToken { get; private set; }

        public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Interlocked.Increment(ref Resolutions);
            DnsEntered.TrySetResult();
            if (HoldDns)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { DnsCancelled = ct.IsCancellationRequested; }
            }
            return Addresses;
        }

        public Task<NetworkPingReply?> SendAsync(IPAddress address, CancellationToken ct)
        {
            var sequence = Interlocked.Increment(ref Sends);
            SendToken = ct;
            SendEntered.TrySetResult();
            return PendingReply?.Task ?? Task.FromResult<NetworkPingReply?>(
                Reply ?? new NetworkPingReply(IPStatus.Success, address, 32, sequence, 128));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _now;
        public bool HasTimers { get { lock (_timers) return _timers.Any(timer => timer.Due.HasValue); } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan duration)
        {
            List<ManualTimer> due;
            lock (_timers)
            {
                _now += duration;
                due = _timers.Where(timer => timer.Due is { } time && time <= _now).ToList();
                foreach (var timer in due) timer.Due = null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? Due { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers) Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._timers) { Due = null; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

