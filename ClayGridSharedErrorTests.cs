using System.Data.Common;
using System.Reflection;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.DynamicGrid;
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
/// CGFR2: exception contract загрузки shared settings (LoadAndValidateSharedParamsAsync).
/// Component-level bUnit через настоящий ClayGrid + URL ?id=101&amp;CLID=1&amp;sharedId=5.
/// Использует fake DB из CGFR1.4 (ScriptedConnection, lazy dequeue).
/// </summary>
public class ClayGridSharedErrorTests : IDisposable
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
    private readonly ScriptedConnection? _currentConn;
    private readonly Queue<ScriptEntry> _globalQueue = new();

    public ClayGridSharedErrorTests()
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

    // ═══ Scripting ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Скрипт shared-mode init: definition, COL_LENGTH, columns, user params,
    /// shared load (передаётся тестом). COUNT/paged/save отсутствуют:
    /// при ошибке guard CGFR2 §13 блокирует data load, при исключении — propagation.
    /// </summary>
    private static List<ScriptEntry> BuildSharedInitScript(
        int gridId, string columnName, string columnHeader, ScriptEntry sharedLoad)
    {
        var s = Schema.Settings;
        var c = Schema.Columns;
        return new List<ScriptEntry>
        {
            Script.Rows(Script.Row((s.GridId, gridId), (s.Title, "Grid"), (s.Icon, DBNull.Value),
                (s.Sql, "SELECT * FROM T"), (s.Id, "ID"), (s.IdName, DBNull.Value),
                (s.EditForm, DBNull.Value), (s.NewForm, DBNull.Value), (s.SqlDelete, DBNull.Value))),
            Script.Rows(Script.Row(("Column1", 0))),
            Script.Rows(Script.Row((c.ColumnId, 1), (c.GridId, gridId), (c.Column, columnName),
                (c.Header, columnHeader), (c.UrlKey, DBNull.Value), (c.Order, 1),
                (c.Format, DBNull.Value), (c.Type, 2))),
            Script.Rows(), // user params (RestoreDynamicState)
            sharedLoad,     // LoadAndValidateSharedParamsAsync
        };
    }

    private void AppendSharedScript(int gridId, ScriptEntry sharedLoad)
    {
        _globalQueue.Clear();
        foreach (var e in BuildSharedInitScript(gridId, "ColumnA", "Колонка A", sharedLoad))
            _globalQueue.Enqueue(e);
        _nav.NavigateTo("http://localhost/?id=101&CLID=1&sharedId=5");
    }

    private static T? GetFld<T>(object instance, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(name, flags)?.GetValue(instance) is T tv) return tv;
        return default;
    }

    // ═══ Тесты ═════════════════════════════════════════════════════════════════

    /// <summary>CGFR2 §6, §17: OperationCanceledException — control flow, НЕ ошибка shared link.
    /// OCE не конвертируется в _dynamicError и не превращается в «База данных недоступна».
    /// Примечание: ComponentBase (.NET 10) молча глотает cancellation из async lifecycle
    /// (Canceled task → teardown semantics) — на уровне renderer OCE невидим по design.
    /// Observable proof: _dynamicError = null, data load не выполняется.</summary>
    [Fact]
    public void SharedLoad_Cancellation_DoesNotBecomeDbError()
    {
        var cancelledToken = new CancellationToken(canceled: true);
        AppendSharedScript(101, Script.Error(new OperationCanceledException("cancelled", cancelledToken)));

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));

        // OCE не превратился в «База данных недоступна» и не в «не найдены»
        Assert.Null(GetFld<string?>(cut.Instance, "_dynamicError"));

        // Data load не выполнялся после cancellation
        Assert.DoesNotContain(_currentConn!.CommandLog,
            c => c.Contains("COUNT"));
    }

    /// <summary>CGFR2 §18: programming exception не маскируется в _dynamicError.</summary>
    [Fact]
    public void SharedLoad_ProgrammingException_Propagates()
    {
        AppendSharedScript(101, Script.Error(new InvalidOperationException("boom")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
                p.Add(c => c.Options, new ClayGridOptions { Dynamic = true })));
        Assert.Equal("boom", ex.Message);
    }

    /// <summary>CGFR2 §19: expected DB failure (non-connectivity SqlException) →
    /// _dynamicError «База данных недоступна», исключения нет, main data SELECT не выполняется.</summary>
    [Fact]
    public void SharedLoad_DbFailure_SetsError_NoDataLoad()
    {
        AppendSharedScript(101, Script.Error(SqlExceptionFactory.Create(547, "FK violation")));

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));

        var error = GetFld<string?>(cut.Instance, "_dynamicError");
        Assert.NotNull(error);
        Assert.Contains("База данных недоступна", error);
        Assert.DoesNotContain("недействительна", error); // §8: не смешивать с invalid link

        // §13: первый SELECT не выполнялся после terminal shared-ошибки
        Assert.DoesNotContain(_currentConn!.CommandLog,
            c => c.Contains("COUNT"));
    }

    /// <summary>CGFR2 §21: пустой результат — terminal «не найдены», без исключения.</summary>
    [Fact]
    public void SharedLoad_Empty_NotFoundMessage()
    {
        AppendSharedScript(101, Script.Rows());

        var cut = _ctx.Render<ClayGrid<ClayDynamicRow>>(p =>
            p.Add(c => c.Options, new ClayGridOptions { Dynamic = true }));

        var error = GetFld<string?>(cut.Instance, "_dynamicError");
        Assert.NotNull(error);
        Assert.Contains("не найдены", error);

        Assert.DoesNotContain(_currentConn!.CommandLog,
            c => c.Contains("COUNT"));
    }
}

// FakeNavigationManager / FakeJSRuntime / SqlExceptionFactory — из соседних test-файлов (тот же namespace).
