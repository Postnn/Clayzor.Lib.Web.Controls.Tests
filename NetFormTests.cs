using System.Security.Claims;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Locations;
using Clayzor.Lib.Web.DBSelect.Phones;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NetPage = Kesco.App.Web.Inventory.Components.Pages.Net.Net;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Сквозные проверки формы порта локальной сети.</summary>
public sealed class NetFormTests
{
    /// <summary>R02/R12/R14: ссылки независимы, не меняют текст и открывают нужное оборудование через clayOpen.</summary>
    [Fact]
    public async Task Load_LinksStayIndependentAndOpenEquipmentWithoutNavigation()
    {
        var fixture = new NetFixture(
            connected: "missing server01",
            description: "server01",
            linkEntry: Script.Rows(Script.Row(("Token", "server01"), ("КодОборудования", 8))));
        await using var context = fixture.Context;
        var page = context.Render<NetPage>();
        WaitForLoad(page);

        var model = Model(page.Instance);
        Assert.Equal("missing server01", model.Connected);
        Assert.Equal("server01", model.Description);
        Assert.Equal("clay-test-net_2", page.Find("[data-testid='net-port-caption']").TextContent);
        var connected = page.Find("[data-testid='net-connected-links']");
        var description = page.Find("[data-testid='net-description-links']");
        Assert.Equal("server01", connected.TextContent.Trim());
        Assert.Equal("server01", description.TextContent.Trim());
        Assert.DoesNotContain("missing", connected.TextContent);
        await connected.QuerySelector("a")!.ClickAsync();

        var invocation = context.JSInterop.Invocations.Single(call => call.Identifier == "clayFormWindow.open");
        Assert.Equal("http://localhost/equipment?id=8", invocation.Arguments[0]);
        Assert.Equal("equipment-form", invocation.Arguments[1]);
        Assert.Equal("http://localhost/Net/Net?id=10", fixture.Navigation.Uri);
    }

    /// <summary>Подвал Changed показывает загруженное имя сотрудника, а не текст Razor-привязки.</summary>
    [Fact]
    public void ChangedFooter_UsesLoadedEmployeeName()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        var changed = page.Find(".clay-changed");
        Assert.Contains("Тестов А.", changed.TextContent);
        Assert.DoesNotContain("_model?.ChangedByName", changed.TextContent);
    }

    /// <summary>Форма использует всю доступную ширину окна, как EquipmentForm и Tel.</summary>
    [Fact]
    public void EntityForm_UsesFullAvailableWidth()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        Assert.Contains("max-width:100%", page.Find(".clay-entity-form").GetAttribute("style"));
        Assert.Contains("max-width:100%", page.Find(".clay-entity-form-footer").GetAttribute("style"));
        Assert.Single(page.FindAll(".mud-grid-item").Where(item => item.ClassList.Contains("equipment-ins-fields")));
    }

    /// <summary>Некорректный ID показывает отсутствие записи без обращения к БД.</summary>
    [Fact]
    public void InvalidId_ShowsNotFoundWithoutSql()
    {
        using var fixture = new NetFixture(id: 0);
        var page = fixture.Context.Render<NetPage>();
        page.WaitForAssertion(() => Assert.Contains("не найден", page.Markup));
        Assert.Empty(fixture.Connection.CommandLog);
    }

    /// <summary>Ошибка resolver не удаляет исходный текст и не блокирует саму форму.</summary>
    [Fact]
    public void LinkResolverError_PreservesLoadedText()
    {
        using var fixture = new NetFixture(connected: "server01",
            linkEntry: new ScriptEntry { Exception = SqlExceptionFactory.Create(53, "connection error") });
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Assert.Equal("server01", Model(page.Instance).Connected);
        Assert.Contains("Не удалось сформировать ссылки", page.Markup);
        Assert.Empty(page.FindAll("[data-testid='net-connected-links']"));
    }

    /// <summary>Розетка без сотрудника и подключённого текста отклоняется до нового SQL.</summary>
    [Fact]
    public async Task SocketValidation_DoesNotWrite()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var count = fixture.Connection.CommandLog.Count;
        Model(page.Instance).SocketId = 5;

        await Invoke(page, "SaveAsync", false);

        Assert.Contains("Вы указали розетку", page.Markup);
        Assert.Equal(count, fixture.Connection.CommandLog.Count);
    }

    /// <summary>R18: Apply записывает, остаётся на форме и строит ссылки из новой сохранённой записи.</summary>
    [Fact]
    public async Task Apply_ReloadsSavedTextAndLinks()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.Enqueue(Script.Scalar(0));
        fixture.AddLoad(connected: "srv-new",
            linkEntry: Script.Rows(Script.Row(("Token", "srv-new"), ("КодОборудования", 8))));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Connected = "srv-new";

        await Invoke(page, "SaveAsync", false);

        page.WaitForAssertion(() => Assert.Equal("srv-new", Model(page.Instance).Connected));
        page.WaitForAssertion(() => Assert.Equal("srv-new",
            page.Find("[data-testid='net-connected-links']").TextContent.Trim()));
        Assert.Contains(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть"));
        Assert.Contains(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>OK без изменений закрывает форму без UPDATE.</summary>
    [Fact]
    public async Task OkWithoutChanges_ClosesWithoutSql()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var count = fixture.Connection.CommandLog.Count;

        await Invoke(page, "SaveAsync", true);

        Assert.Equal(count, fixture.Connection.CommandLog.Count);
        Assert.Contains(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Сотрудник подставляет организацию без очистки заказчика при сбросе, розетка управляет своим примечанием.</summary>
    [Fact]
    public async Task EmployeeAndSocketChanges_PreserveLegacyDependencies()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 2))));
        fixture.Enqueue(Script.Rows(Script.Row(("КодРозетки", 5), ("Розетка", "A-1"),
            ("Примечание", "socket note"), ("КодРасположения", 55), ("Работает", true))));
        fixture.Enqueue(Script.Rows(Script.Row(("КодЛица", 2), ("Кличка", "Организация 2"))));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        await Invoke(page, "OnEmployeeChangedAsync", 4);
        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
        await Invoke(page, "OnEmployeeChangedAsync", (int?)null);
        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
        await Invoke(page, "OnSocketChangedAsync", 5);
        Assert.Equal("socket note", GetPrivate<string>(page.Instance, "_socketNotes"));
        await Invoke(page, "OnSocketChangedAsync", (int?)null);
        Assert.Null(GetPrivate<string>(page.Instance, "_socketNotes"));
        Render(page);
        Assert.Equal(2, Model(page.Instance).PersonCustomerId);
    }

    /// <summary>Выбор переключения/обмена сбрасывает альтернативу и скрывает её строку.</summary>
    [Fact]
    public async Task PortModes_AreMutuallyExclusive()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);

        await Invoke(page, "OnSwitchChanged", 11);
        Render(page);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-testid='net-exchange-row']")));
        Assert.Equal(-1, GetPrivate<int?>(page.Instance, "_exchangeWithId"));
        await Invoke(page, "OnExchangeChanged", 12);
        Render(page);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-testid='net-switch-row']")));
        Assert.Equal(-1, GetPrivate<int?>(page.Instance, "_switchToId"));
    }

    /// <summary>Выключение срока возврата очищает дату; просроченная дата остаётся предупреждением.</summary>
    [Fact]
    public async Task ReturnDateCheckbox_ClearsDraftAndShowsLegacyWarning()
    {
        using var fixture = new NetFixture();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).ReturnDate = DateTime.Today.AddDays(-1);

        await Invoke(page, "OnReturnDateEnabledChanged", true);
        Render(page);
        page.WaitForAssertion(() => Assert.Contains("просрочено", page.Markup));
        await Invoke(page, "OnReturnDateEnabledChanged", false);
        Render(page);

        Assert.Null(Model(page.Instance).ReturnDate);
        Assert.Empty(page.FindAll(".mud-picker"));
    }

    /// <summary>Неизвестный или утративший идентичность пользователь не выполняет запись.</summary>
    [Fact]
    public async Task SaveWithoutCurrentIdentity_FailsClosed()
    {
        using var fixture = new NetFixture(currentUser: false);
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Description = "draft";
        var count = fixture.Connection.CommandLog.Count;

        await Invoke(page, "SaveAsync", false);

        Assert.Equal(count, fixture.Connection.CommandLog.Count);
        Assert.Contains("Текущий пользователь не определён", page.Markup);
    }

    /// <summary>PoE требует подтверждение, использует stub с timeout 120 и снимает overlay.</summary>
    [Fact]
    public async Task Poe_ConfirmationUsesStubAndAlwaysEndsOverlay()
    {
        using var fixture = new NetFixture(poe: 1);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.Enqueue(Script.NonQuery());
        var provider = fixture.Context.Render<MudDialogProvider>();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "RestartPoeAsync");
        provider.WaitForAssertion(() => Assert.Contains("Перезапустить PoE", provider.Markup));

        await page.InvokeAsync(() => provider.FindAll("button").Single(button => button.TextContent.Trim() == "Да").Click());
        await pending;

        Assert.Equal(120, fixture.Connection.TimeoutLog.Single());
        Assert.False(GetPrivate<bool>(page.Instance, "_poeRunning"));
        Assert.Empty(page.FindAll(".mud-alert").Where(alert => alert.TextContent.Contains("Ошибка при перезапуске PoE")));
    }

    /// <summary>Исчезновение текущей идентичности перед записью запрещает UPDATE.</summary>
    [Fact]
    public async Task IdentityLostAfterLoad_DoesNotWrite()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows());
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Description = "draft";

        await Invoke(page, "SaveAsync", false);

        Assert.Contains("Сохранение запрещено", page.Markup);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть"));
    }

    /// <summary>R18: ошибка записи сохраняет черновик и не закрывает форму.</summary>
    [Fact]
    public async Task SaveConnectivityError_PreservesDraftWithoutClose()
    {
        using var fixture = new NetFixture();
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.Enqueue(new ScriptEntry { Exception = SqlExceptionFactory.Create(53, "connection error") });
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        Model(page.Instance).Description = "unsaved draft";

        await Invoke(page, "SaveAsync", false);

        Assert.Equal("unsaved draft", Model(page.Instance).Description);
        Assert.Contains("Результат сохранения локальной сети неизвестен", page.Markup);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Ошибка stub PoE показывает сообщение и всегда снимает overlay.</summary>
    [Fact]
    public async Task PoeStubError_ShowsErrorAndEndsOverlay()
    {
        using var fixture = new NetFixture(poe: 1);
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
            ("Employee", "Test"), ("КодЛицаЗаказчика", 1))));
        fixture.Enqueue(new ScriptEntry { Exception = SqlExceptionFactory.Create(50000, "stub error") });
        var provider = fixture.Context.Render<MudDialogProvider>();
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        var pending = Invoke(page, "RestartPoeAsync");
        provider.WaitForAssertion(() => Assert.Contains("Перезапустить PoE", provider.Markup));

        await page.InvokeAsync(() => provider.FindAll("button").Single(button => button.TextContent.Trim() == "Да").Click());
        await pending;

        Assert.Contains("Ошибка при перезапуске PoE", page.Markup);
        Assert.False(GetPrivate<bool>(page.Instance, "_poeRunning"));
    }

    /// <summary>R19: поздний resolver после dispose не записывает ссылки в закрытую форму.</summary>
    [Fact]
    public async Task LateResolverAfterDispose_DoesNotApplyLinks()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new NetFixture(connected: "server01", linkEntry: new ScriptEntry
        {
            Started = started,
            Release = release,
            Rows = [Script.Row(("Token", "server01"), ("КодОборудования", 8))],
        });
        var page = fixture.Context.Render<NetPage>();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        page.Instance.Dispose();
        release.SetResult();
        await Task.Delay(100);

        Assert.Empty(page.FindAll("[data-testid='net-connected-links']"));
    }

    private static void Render(IRenderedComponent<NetPage> page) => page.InvokeAsync(() =>
        typeof(NetPage).GetMethod("StateHasChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(page.Instance, null));

    private static void WaitForLoad(IRenderedComponent<NetPage> page) =>
        page.WaitForAssertion(() => Assert.Equal("clay-test-net_2",
            page.Find("[data-testid='net-port-caption']").TextContent), TimeSpan.FromSeconds(3));

    private static LocalNetworkPort Model(NetPage page) => GetPrivate<LocalNetworkPort>(page, "_model")!;

    private static T? GetPrivate<T>(NetPage page, string name) => (T?)typeof(NetPage)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page);

    private static Task Invoke(IRenderedComponent<NetPage> page, string name, params object?[] args) =>
        page.InvokeAsync(async () =>
        {
            var method = typeof(NetPage).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, args) is Task task)
                await task;
        });

    private sealed class NetFixture : IDisposable
    {
        private readonly Queue<ScriptEntry> _entries;

        public NetFixture(
            int id = 10,
            string connected = "",
            string description = "",
            byte poe = 0,
            bool currentUser = true,
            ScriptEntry? linkEntry = null)
        {
            DapperColumnMapper.Initialize();
            _entries = new Queue<ScriptEntry>();
            AddLoad(connected, description, poe, currentUser, linkEntry);
            Connection = new ScriptedConnection(_entries);
            Context = new BunitContext();
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            Context.Services.AddMudServices();
            Context.Services.AddMudExtensions();
            var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => Connection);
            Context.Services.AddScoped(_ => db);
            Context.Services.AddSingleton(new NetSocketSelectDataSource(db));
            Context.Services.AddSingleton(new PhoneEmployeeSelectDataSource(db));
            Context.Services.AddSingleton(new PhoneCustomerSelectDataSource(db));
            Context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["URI_equipment_form"] = "http://localhost/equipment",
                    ["URI_user_form"] = "http://localhost/user",
                }).Build());
            Context.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthenticationStateProvider());
            Context.Services.AddSingleton(new ClayFormWindowService(Context.JSInterop.JSRuntime));
            Navigation = new FakeNavigationManager();
            Navigation.NavigateTo($"http://localhost/Net/Net?id={id}");
            Context.Services.AddSingleton<NavigationManager>(Navigation);
        }

        public BunitContext Context { get; }
        public ScriptedConnection Connection { get; }
        public FakeNavigationManager Navigation { get; }

        public void AddLoad(
            string connected = "",
            string description = "",
            byte poe = 0,
            bool currentUser = true,
            ScriptEntry? linkEntry = null)
        {
            _entries.Enqueue(currentUser
                ? Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
                    ("Employee", "Test"), ("КодЛицаЗаказчика", 1)))
                : Script.Rows());
            _entries.Enqueue(Script.Rows(Script.Row(
                ("КодЛокальнойСети", 10), ("КодОборудования", 8), ("Порт", (byte)2), ("PoE", poe),
                ("КодРозетки", null), ("КодЛицаЗаказчика", 1), ("КодСотрудника", null),
                ("Подключено", connected), ("Примечания", description), ("СрокВозврата", null),
                ("Изменил", 4), ("Сотрудник", "Тестов А."), ("Изменено", new DateTime(2026, 11, 21, 10, 0, 0)))));
            _entries.Enqueue(Script.Rows(Script.Row(("СетевоеИмя", "clay-test-net"))));
            _entries.Enqueue(Script.Rows(Script.Row(("КодРасположения", 50))));
            _entries.Enqueue(Script.Rows(
                Script.Row(("КодЛокальнойСети", 11), ("Строка", "sw-a, порт:1"))));
            _entries.Enqueue(Script.Rows(
                Script.Row(("КодЛокальнойСети", 11), ("Строка", "sw-a, порт:1")),
                Script.Row(("КодЛокальнойСети", 12), ("Строка", "sw-a, порт:2"))));
            if (NetEquipmentLinkExtractor.Extract(connected).Count > 0 || NetEquipmentLinkExtractor.Extract(description).Count > 0)
                _entries.Enqueue(linkEntry ?? Script.Rows());
            _entries.Enqueue(Script.Rows(Script.Row(("КодЛица", 1), ("Кличка", "Тестовая организация"))));
        }

        public void Enqueue(ScriptEntry entry) => _entries.Enqueue(entry);

        public void Dispose() => Context.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class FakeAuthenticationStateProvider : AuthenticationStateProvider
    {
        /// <inheritdoc />
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\devadmin")], "Negotiate");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
