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

    [Fact]
    public void InitException_AllowsRetry()
    {
        // Первая попытка: definition load бросает исключение
        _globalQueue.Clear();
        _globalQueue.Enqueue(Script.Error(new InvalidOperationException("boom")));
        for (int i = 0; i < 7; i++) _globalQueue.Enqueue(Script.Rows());
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");

        Assert.Throws<InvalidOperationException>(() =>
            _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
                p.Add(c => c.Options, new ClayGridOptions { Dynamic = true, DynamicGridId = 101 })));

        // Очистить остатки от первой попытки и добавить success-скрипт
        _globalQueue.Clear();
        var connOk = AppendScript(BuildInitScript(101, "Grid 101", "SELECT * FROM T", "ColOk", "Колонка OK", 1));
        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true, DynamicGridId = 101 }));

        Assert.NotNull(cut.Instance.GetColumnMeta("ColOk"));
        // DefCount включает failed попытку → >1. Проверяем только успех колонки.
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
}

internal sealed class FakeNavigationManager : NavigationManager
{
    public FakeNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    public void NavigateTo(string uri) { Uri = uri; NotifyLocationChanged(false); }
    protected override void NavigateToCore(string uri, bool forceLoad) => Uri = uri;
}

internal sealed class FakeJSRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => new(default(TValue)!);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        => new(default(TValue)!);
}
