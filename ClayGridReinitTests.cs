using System.Data.Common;
using System.Reflection;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.DynamicGrid;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Clayzor.Lib.Web.Settings;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// CGFR1.1 bUnit lifecycle tests (CGFR1.1 §8–§15).
/// Реальный Blazor render через <c>_ctx.Render&lt;T&gt;()</c> + <c>cut.Render(p =&gt; ...)</c>.
/// Фейковый DB через <c>ScriptedConnection</c> + internal seam <c>DbManager</c>.
/// Все сервисы регистрируются ДО первого render.
/// </summary>
public class ClayGridReinitTests : IDisposable
{
    private readonly TestContext _ctx = new();
    private readonly FakeNavigationManager _nav;
    private readonly ClayErrorService _errorService = new();
    private readonly ClayGridDynamicSettings _settings = new()
    {
        ConnectionStringName = "DefaultConnection",
        SettingsTable = "ClayGridSettings",
        ColumnsTable = "ClayGridColumns",
        UserParamsTable = "ClayGridUserParams",
        UserSharedParamsTable = "ClayGridUserSharedParams",
        UserParamsShared = "ClayGridUserParamsShared",
        GridIdQueryParam = "id",
        ClientIdQueryParam = "CLID",
    };
    private static readonly ClayGridSchemaMap Schema = new();

    // Один ScriptedConnection на тест — обе очереди (A + B) кладутся в него подряд.
    // DbManager кеширует DbConnection → переиспользование одного экземпляра корректно.
    private ScriptedConnection? _currentConn;
    private Queue<ScriptEntry> _globalQueue = new();

    public ClayGridReinitTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddMudExtensions();
        _ctx.Services.AddSingleton<ISqlErrorHandler>(_errorService);
        _ctx.Services.AddSingleton<ClayErrorService>(_errorService);
        _ctx.Services.AddSingleton(new ClayAppSettings());
        _ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        _ctx.Services.AddSingleton(Options.Create(_settings));
        _ctx.Services.AddSingleton<IJSRuntime>(new FakeJSRuntime());
        _ctx.Services.AddSingleton<Clayzor.Lib.Web.Controls.Services.ClayFormWindowService>(sp =>
            new(sp.GetRequiredService<IJSRuntime>()));
        _nav = new FakeNavigationManager();
        _ctx.Services.AddSingleton<NavigationManager>(_nav);
        var conn = new ScriptedConnection(_globalQueue);
        _currentConn = conn;
        _ctx.Services.AddSingleton<DbManager>(_ =>
            new DbManager("Server=test", _errorService, () => conn));
    }

    public void Dispose()
    {
        try { _ctx.Dispose(); } catch { }
    }

    // ═══ DB scripting ═══════════════════════════════════════════════════════════

    private static List<ScriptEntry> BuildInitScript(int gridId, string title, string selectSql,
        string columnName, string columnHeader, int columnId, int totalCount = 0)
    {
        var s = Schema.Settings;
        var c = Schema.Columns;
        return new List<ScriptEntry>
        {
            Script.Rows(Script.Row((s.GridId, gridId), (s.Title, title), (s.Icon, DBNull.Value),
                (s.Sql, selectSql), (s.Id, "ID"), (s.IdName, DBNull.Value),
                (s.EditForm, DBNull.Value), (s.NewForm, DBNull.Value), (s.SqlDelete, DBNull.Value))),
            Script.Rows(Script.Row(("Column1", 0))),
            Script.Rows(Script.Row((c.ColumnId, columnId), (c.GridId, gridId), (c.Column, columnName),
                (c.Header, columnHeader), (c.UrlKey, DBNull.Value), (c.Order, 1),
                (c.Format, DBNull.Value), (c.Type, 2))),
            Script.Rows(), // user params
            Script.Rows(), // shared check
            Script.Rows(Script.Row(("cnt", totalCount))),
            Script.Rows(),
            Script.NonQuery(),
        };
    }

    /// <summary>Добавляет записи в глобальную очередь и возвращает shared-connection для CommandLog.</summary>
    private ScriptedConnection AppendScript(IEnumerable<ScriptEntry> entries)
    {
        foreach (var e in entries)
            _globalQueue.Enqueue(e);
        return _currentConn!;
    }

    // ═══ Reflection ═════════════════════════════════════════════════════════════

    private static T? GetFld<T>(object instance, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(name, flags)?.GetValue(instance) is T tv) return tv;
        return default;
    }

    /// <summary>Definition load count из ScriptedConnection.CommandLog.</summary>
    private static int DefCount(ScriptedConnection conn) =>
        conn.CommandLog.Count(c => c.Contains("FROM [ClayGridSettings]"));

    // ═══ Тесты ═════════════════════════════════════════════════════════════════

    /// <summary>Ошибка SQL не разрывает render/circuit и может быть повторена той же компонентой.</summary>
    [Fact]
    public void SqlError_ShowsRecoverableAlertAndRetriesSameInstance()
    {
        AppendScript([Script.Error(SqlExceptionFactory.Create(208, "Invalid object"))]);
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(parameters =>
            parameters.Add(component => component.Options, new ClayGridOptions { Dynamic = true }));

        Assert.Contains("Не удалось загрузить данные запроса Список #101.", cut.Markup);
        Assert.Contains("Ошибка: Invalid object", cut.Markup);
        Assert.Contains("Строка подключения:", cut.Markup);
        Assert.Contains("test", cut.Markup);
        Assert.False(GetFld<bool>(cut.Instance, "_isExporting"));
        var instance = cut.Instance;

        AppendScript(BuildInitScript(101, "Grid A", "SELECT * FROM A", "ColumnA", "Колонка A", 1));
        cut.Find("button").Click();
        cut.WaitForState(() => _globalQueue.Count == 0, TimeSpan.FromSeconds(10));

        Assert.Same(instance, cut.Instance);
        Assert.NotNull(cut.Instance.GetColumnMeta("ColumnA"));
        Assert.Null(GetFld<string?>(cut.Instance, "_dynamicError"));
    }

    /// <summary>Из строки подключения маскируются только значения Password/Pwd.</summary>
    [Theory]
    [InlineData("Server=sql01;Database=Inventory;User Id=user;Password=secret")]
    [InlineData("Pwd=secret;Server=sql01;Database=Inventory")]
    [InlineData("Server=sql01;Database=Inventory;Password=secret;Broken")]
    public void ConnectionStringError_MaskOnlyPasswordValue(string connectionString)
    {
        var masked = ClayGrid<ClayDynamicRow>.MaskConnectionStringPassword(connectionString);

        Assert.DoesNotContain("secret", masked);
        Assert.Contains("***", masked);
        Assert.Contains("sql01", masked);
        Assert.Contains("Inventory", masked);
    }

    /// <summary>Integrated Security не добавляет отсутствующий пароль в сообщение.</summary>
    [Fact]
    public void ConnectionStringError_DoesNotAddPasswordWhenAbsent()
    {
        var masked = ClayGrid<ClayDynamicRow>.MaskConnectionStringPassword(
            "Server=localhost;Database=Inventory;Integrated Security=true");

        Assert.DoesNotContain("Password", masked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pwd", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localhost", masked);
        Assert.Contains("Inventory", masked);
    }

    [Fact]
    public void AtoB_SameInstance_ColumnsReplaced()
    {
        var connA = AppendScript(BuildInitScript(101, "Grid A", "SELECT * FROM A", "ColumnA", "Колонка A", 1));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));
        var instance = cut.Instance;
        Assert.NotNull(instance.GetColumnMeta("ColumnA"));
        Assert.Equal(1, DefCount(connA));
        Assert.NotNull(instance.GetColumnMeta("ColumnA"));

        var connB = AppendScript(BuildInitScript(202, "Grid B", "SELECT * FROM B", "ColumnB", "Колонка B", 2));
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");
        cut.Render(p => p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));

        Assert.Same(instance, cut.Instance);
        Assert.Null(instance.GetColumnMeta("ColumnA"));
        Assert.NotNull(instance.GetColumnMeta("ColumnB"));
    }

    /// <summary>После переинициализации A→B busy-overlay обязан быть снят.</summary>
    [Fact]
    public void AtoB_BusyOverlayClearedAfterLoad()
    {
        var titles = new List<string>();
        AppendScript(BuildInitScript(101, "Grid A", "SELECT * FROM A", "ColumnA", "Колонка A", 1));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(parameters =>
            parameters.Add(component => component.Options, new ClayGridOptions { Dynamic = true })
                .Add(component => component.TitleChanged,
                    EventCallback.Factory.Create<string>(this, title => titles.Add(title))));
        cut.WaitForState(() => _globalQueue.Count == 0, TimeSpan.FromSeconds(10));

        AppendScript(BuildInitScript(202, "Grid B", "SELECT * FROM B", "ColumnB", "Колонка B", 2));
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");
        cut.Render(parameters => parameters.Add(component => component.Options, new ClayGridOptions { Dynamic = true }));
        cut.WaitForState(() => _globalQueue.Count == 0, TimeSpan.FromSeconds(10));

        Assert.False(GetFld<bool>(cut.Instance, "_isExporting"));
        Assert.Null(GetFld<string?>(cut.Instance, "_busyLabel"));
        Assert.Equal("Grid B", titles.LastOrDefault());
    }

    /// <summary>Два параллельных запуска одной identity не смешивают колонки и не читают definition дважды.</summary>
    [Fact]
    public async Task ConcurrentSameIdentityInit_UsesSingleDefinitionAndUniqueColumns()
    {
        AppendScript(BuildInitScript(101, "Grid A", "SELECT * FROM A", "ColumnA", "Колонка A", 1));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(parameters =>
            parameters.Add(component => component.Options, new ClayGridOptions { Dynamic = true }));
        cut.WaitForState(() => _globalQueue.Count == 0, TimeSpan.FromSeconds(10));

        var connection = AppendScript(BuildInitScript(202, "Grid B", "SELECT * FROM B", "ColumnB", "Колонка B", 2));
        var definitionsBeforeB = DefCount(connection);
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");
        var method = typeof(ClayGrid<ClayDynamicRow>).GetMethod("OnParametersSetAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, null)!);
        var second = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, null)!);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForState(() => _globalQueue.Count == 0, TimeSpan.FromSeconds(10));

        Assert.Equal(definitionsBeforeB + 1, DefCount(connection));
        var columns = GetFld<List<int>>(cut.Instance, "_columnOrder")!;
        Assert.Equal(columns.Count, columns.Distinct().Count());
        Assert.False(GetFld<bool>(cut.Instance, "_isExporting"));
    }

    [Fact]
    public void SameKey_PresentationChange_NoReinit()
    {
        var conn = AppendScript(BuildInitScript(101, "Original", "SELECT * FROM T", "ColA", "Колонка A", 1, totalCount: 5));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true, DynamicGridId = 101 }));
        Assert.Equal(1, DefCount(conn));

        cut.Render(p => p.Add(c => c.Options,
            new ClayGridOptions { Dynamic = true, DynamicGridId = 101, Title = "Changed" }));
        Assert.Equal(1, DefCount(conn));
    }

    [Fact]
    public void UrlChange_QueryCache_Refreshes()
    {
        var connA = AppendScript(BuildInitScript(101, "Grid 101", "SELECT * FROM T101", "Col101", "Колонка 101", 1));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));
        Assert.NotNull(cut.Instance.GetColumnMeta("Col101"));

        var connB = AppendScript(BuildInitScript(202, "Grid 202", "SELECT * FROM T202", "Col202", "Колонка 202", 2));
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");
        cut.Render(p => p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));

        Assert.NotNull(cut.Instance.GetColumnMeta("Col202"));
        Assert.Null(cut.Instance.GetColumnMeta("Col101"));
    }

    [Fact]
    public void SameMutableOptions_DynamicGridIdChanged_Reinit()
    {
        var connA = AppendScript(BuildInitScript(101, "Grid 101", "SELECT * FROM T101", "ColA", "Колонка A", 1));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        var options = new ClayGridOptions { Dynamic = true, DynamicGridId = 101 };
        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p => p.Add(c => c.Options, options));
        Assert.NotNull(cut.Instance.GetColumnMeta("ColA"));

        options.DynamicGridId = 202;
        var connB = AppendScript(BuildInitScript(202, "Grid 202", "SELECT * FROM T202", "ColB", "Колонка B", 2));
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");
        cut.Render(p => p.Add(c => c.Options, options));

        Assert.NotNull(cut.Instance.GetColumnMeta("ColB"));
        Assert.Null(cut.Instance.GetColumnMeta("ColA"));
    }

    /// <summary>CGFR1.4: A success → B reinit fails → B retry succeeds на том же instance.
    /// Доказывает через fake DB CommandLog: failed init НЕ коммитит ключ,
    /// повторный render той же identity выполняет повторную инициализацию →
    /// ровно 2 B definition attempts.</summary>
    [Fact]
    public async Task FailedReinit_RetriesSameIdentity_OnSameComponentInstance()
    {
        // ── 1. Grid 101 успешно ──
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var conn = AppendScript(BuildInitScript(101, "Grid A", "SELECT * FROM A", "ColumnA", "Колонка A", 1));

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));
        var instance = cut.Instance;
        Assert.NotNull(instance.GetColumnMeta("ColumnA"));
        int defAfterA = DefCount(conn); // снапшот definition loads после A

        // Дождаться полного завершения async lifecycle A render:
        // RunBusyAsync содержит await Task.Yield() — хвост InitDynamicMode
        // (COUNT/paged/save) выполняется на thread pool ПОСЛЕ возврата Render().
        // Без ожидания хвостовые DB-вызовы A потребляют entries следующего скрипта.
        cut.WaitForState(() => _globalQueue.Count == 0,
            TimeSpan.FromSeconds(10));

        // ── 2. Grid 202: fail + retry в ОДНОЙ очереди (без Clear между рендерами) ──
        // Error: explicit render (definition load). Valid x 8: retry.
        _globalQueue.Clear();
        _globalQueue.Enqueue(Script.Error(new InvalidOperationException("boom")));
        foreach (var e in BuildInitScript(202, "Grid B", "SELECT * FROM B", "ColumnB", "Колонка B", 2))
            _globalQueue.Enqueue(e);
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");

        // Первая B-попытка: definition load падает → исключение из OnParametersSetAsync.
        // bUnit Render() пробрасывает его наружу.
        Assert.Throws<InvalidOperationException>(() =>
            cut.Render(p => p.Add(c => c.Options,
                new ClayGridOptions { Dynamic = true })));

        Assert.Same(instance, cut.Instance);
        Assert.Null(instance.GetColumnMeta("ColumnA"));

        // ── 3. Retry того же Grid 202 — без try/catch, должен преуспеть ──
        cut.Render(p => p.Add(c => c.Options,
            new ClayGridOptions { Dynamic = true }));

        Assert.Same(instance, cut.Instance);
        Assert.NotNull(instance.GetColumnMeta("ColumnB"));
        Assert.Null(instance.GetColumnMeta("ColumnA"));

        // Ровно 2 B definition attempts: failed (1) + retry (1)
        Assert.Equal(2, DefCount(conn) - defAfterA);
    }

    [Fact]
    public void AfterSuccess_SameKey_NoReinit()
    {
        var conn = AppendScript(BuildInitScript(101, "Grid", "SELECT * FROM T", "ColA", "Колонка A", 1, totalCount: 3));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true, DynamicGridId = 101 }));
        Assert.Equal(1, DefCount(conn));

        cut.Render(p => p.Add(c => c.Options,
            new ClayGridOptions { Dynamic = true, DynamicGridId = 101 }));
        Assert.Equal(1, DefCount(conn));
    }

    [Fact]
    public void DynamicToStatic_ResetsState()
    {
        var conn = AppendScript(BuildInitScript(101, "Dynamic", "SELECT * FROM T", "ColA", "Колонка A", 1));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true, DynamicGridId = 101 }));
        Assert.NotNull(cut.Instance.GetColumnMeta("ColA"));

        cut.Render(p => p.Add(c => c.Options, new ClayGridOptions { Dynamic = false }));

        Assert.Null(cut.Instance.GetColumnMeta("ColA"));
        Assert.Null(GetFld<ClayGridDynamicKey?>(cut.Instance, "_currentDynamicKey"));
    }

    /// <summary>Очистка через toolbar сохраняет дефолты и не возвращает старое состояние из кеша.</summary>
    [Fact]
    public async Task ClearDynamicState_WritesDefaultsWithoutStaleCacheRestore()
    {
        var connection = AppendScript(BuildInitScript(101, "Grid", "SELECT * FROM T", "ColA", "Колонка A", 1));
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var provider = _ctx.Render<MudBlazor.MudDialogProvider>();
        var popovers = _ctx.Render<MudBlazor.MudPopoverProvider>();
        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(parameters =>
            parameters.Add(component => component.Options, new ClayGridOptions { Dynamic = true, PageSize = 50 }));
        cut.WaitForState(() => _globalQueue.Count == 0, TimeSpan.FromSeconds(10));
        cut.Find(".toolbar-batch-btn button").Click();
        popovers.WaitForAssertion(() => Assert.Contains("Очистить параметры формы", popovers.Markup));

        SetField(cut.Instance, "_pageSize", 99);
        SetField(cut.Instance, "_searchText", "stale");
        GetFld<List<string>>(cut.Instance, "_groupColumns")!.Add("ColA");
        GetFld<Dictionary<string, string>>(cut.Instance, "_dynamicSavedParams")!["pgs101"] = "99";
        _globalQueue.Enqueue(Script.NonQuery());
        _globalQueue.Enqueue(Script.Rows(Script.Row(("cnt", 0))));
        _globalQueue.Enqueue(Script.Rows());
        var dialogAdded = new TaskCompletionSource<MudBlazor.IDialogReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ctx.Services.GetRequiredService<MudBlazor.IDialogService>().DialogInstanceAddedAsync += reference =>
        {
            dialogAdded.TrySetResult(reference);
            return Task.CompletedTask;
        };

        var pending = cut.InvokeAsync(() =>
            (Task)typeof(ClayGrid<ClayDynamicRow>).GetMethod("ClearDynamicStateAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cut.Instance, null)!);
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.WaitForAssertion(() => Assert.Contains("Вы действительно хотите очистить параметры формы?", provider.Markup));
        await cut.InvokeAsync(() => dialog.Close(MudBlazor.DialogResult.Ok(true)));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForState(() => _globalQueue.Count == 0, TimeSpan.FromSeconds(10));

        Assert.Equal(50, GetFld<int>(cut.Instance, "_pageSize"));
        Assert.Null(GetFld<string?>(cut.Instance, "_searchText"));
        Assert.Empty(GetFld<List<string>>(cut.Instance, "_groupColumns")!);
        Assert.Equal("50", GetFld<Dictionary<string, string>>(cut.Instance, "_dynamicSavedParams")!["pgs101"]);
        Assert.DoesNotContain(connection.ParameterLog, parameters =>
            parameters.Values.Any(value => string.Equals(value?.ToString(), "99", StringComparison.Ordinal)));
    }

    private static void SetField(object instance, string name, object? value)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, flags);
            if (field is null) continue;
            field.SetValue(instance, value);
            return;
        }
        throw new MissingFieldException(instance.GetType().Name, name);
    }
}

internal sealed class FakeNavigationManager : NavigationManager
{
    public FakeNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    public void NavigateTo(string uri) { Uri = uri; NotifyLocationChanged(false); }
    protected override void NavigateToCore(string uri, bool forceLoad) => Uri = uri;
    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        Uri = ToAbsoluteUri(uri).ToString();
        NotifyLocationChanged(false);
    }
    protected override void SetNavigationLockState(bool value) { }
}

internal sealed class FakeJSRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => new(default(TValue)!);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        => new(default(TValue)!);
}
