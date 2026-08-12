using System.Collections.Specialized;
using System.Reflection;
using System.Web;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.DynamicGrid;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Clayzor.Lib.Web.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// CGFR1 lifecycle tests (CGFR1 §28–§34).
/// Тестируют OnParametersSetAsync, ResetDynamicRuntimeState и Query cache
/// через ручной инжект + рефлексию. Полный MudBlazor render pipeline (MudDataGrid,
/// PopoverService) требует Blazor Dispatcher, недоступного в unit-тестах без
/// сложной инфраструктуры; CGFR1 §34 разрешает internal seam + reflection.
/// </summary>
public class ClayGridReinitTests : IDisposable
{
    private readonly ServiceProvider _sp;
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

    public ClayGridReinitTests()
    {
        _nav = new FakeNavigationManager();
        var services = new ServiceCollection();
        services.AddMudServices();
        services.AddMudExtensions();
        services.AddSingleton<ISqlErrorHandler>(_errorService);
        services.AddSingleton<ClayErrorService>(_errorService);
        services.AddSingleton(new ClayAppSettings());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(_settings));
        services.AddSingleton<NavigationManager>(_nav);
        services.AddSingleton<IJSRuntime>(new FakeJSRuntime());
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    // ═══ Reflection ════════════════════════════════════════════════════════════

    private static T? GetFld<T>(object instance, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField(name, flags);
            if (f is null) continue;
            var v = f.GetValue(instance);
            if (v is T tv) return tv;
            // unbox value types
            if (v is not null && typeof(T).IsValueType)
            {
                try { return (T)v; } catch { return default; }
            }
            return default;
        }
        return default;
    }

    private static T? GetProp<T>(object instance, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t is not null; t = t.BaseType)
        {
            var p = t.GetProperty(name, flags);
            if (p is null) continue;
            var v = p.GetValue(instance);
            if (v is T tv) return tv;
            return default;
        }
        return default;
    }

    private static void SetFld(object instance, string name, object value)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField(name, flags);
            var p = t.GetProperty(name, flags);
            if (f is not null) { f.SetValue(instance, value); return; }
            if (p is not null) { p.SetValue(instance, value); return; }
        }
    }

    private static async Task CallOnParamsSetAsync(object instance)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t is not null; t = t.BaseType)
        {
            var m = t.GetMethod("OnParametersSetAsync", flags);
            if (m is null) continue;
            var r = m.Invoke(instance, null);
            if (r is Task task) await task;
            return;
        }
    }

    private static void CallReset(object instance)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var t = instance.GetType();
        var m = t.GetMethod("ResetDynamicRuntimeState", flags)
                ?? t.BaseType?.GetMethod("ResetDynamicRuntimeState", flags);
        m?.Invoke(instance, null);
    }

    private ClayGrid<ClayDynamicRow> CreateGrid(ClayGridOptions options)
    {
        var grid = ActivatorUtilities.CreateInstance<ClayGrid<ClayDynamicRow>>(_sp);
        SetFld(grid, "DynamicOpts", _sp.GetRequiredService<IOptions<ClayGridDynamicSettings>>());
        SetFld(grid, "Nav", _nav);
        SetFld(grid, "Config", _sp.GetRequiredService<IConfiguration>());
        SetFld(grid, "ClaySettings", _sp.GetRequiredService<ClayAppSettings>());
        SetFld(grid, "ErrorService", _errorService);
        SetFld(grid, "_opt", options);

        // Db НЕ инжектим — InitDynamicMode будет падать на первом же DB-вызове,
        // но OnParametersSetAsync всё равно строит ключ и проверяет до вызова InitDynamicMode.
        // Для тестов, проверяющих reset/reinit без БД, Db не нужен.

        return grid;
    }

    // ═══ Тесты ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task KeyChanged_TriggersReset()
    {
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var grid = CreateGrid(new ClayGridOptions { Dynamic = true, DynamicGridId = 101 });
        SetFld(grid, "_dynamicDef", new ClayGridDefinition(101, "A", null, "SELECT 1", null, null, null, null, null, false));
        SetFld(grid, "_columnBySqlName", new Dictionary<string, ClayColumnMeta> { ["ColA"] = new() { ColumnId = 1, SqlName = "ColA", DisplayName = "ColumnA" } });

        var before = GetFld<object>(grid, "_dynamicDef");
        Assert.NotNull(before);

        // Меняем ключ
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");

        // OnParametersSetAsync должен задетектить смену ключа и вызвать Reset
        // Но без Db вызов InitDynamicMode упадёт на DB-вызове.
        // Проверяем, что Reset отработал до попытки InitDynamicMode.
        try { await CallOnParamsSetAsync(grid); }
        catch (NullReferenceException) { } // ожидаемо: нет Db

        // После reset _dynamicDef должен быть null
        var after = GetFld<object>(grid, "_dynamicDef");
        Assert.Null(after);
        Assert.Empty(GetFld<Dictionary<string, ClayColumnMeta>>(grid, "_columnBySqlName") ?? []);
    }

    [Fact]
    public async Task SameKey_NoReset()
    {
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var grid = CreateGrid(new ClayGridOptions { Dynamic = true, DynamicGridId = 101 });
        SetFld(grid, "_dynamicDef", new ClayGridDefinition(101, "A", null, "SELECT 1", null, null, null, null, null, false));

        try { await CallOnParamsSetAsync(grid); } catch { } // первый вызов

        var def = GetFld<object>(grid, "_dynamicDef");
        Assert.Null(def); // сброшен, т.к. первый вызов был с null key → reset + init (падает)

        // Устанавливаем _currentDynamicKey вручную — симулируем, что уже инициализировались
        var key = ClayGridDynamicKey.Create(101, 1, null, _settings);
        SetFld(grid, "_currentDynamicKey", key);
        SetFld(grid, "_dynamicDef", new ClayGridDefinition(101, "A", null, "SELECT 1", null, null, null, null, null, false));

        // Вызываем снова с тем же ключом
        await CallOnParamsSetAsync(grid);

        // _dynamicDef НЕ сброшен
        Assert.NotNull(GetFld<object>(grid, "_dynamicDef"));
        Assert.NotNull(GetFld<ClayGridDynamicKey?>(grid, "_currentDynamicKey"));
    }

    [Fact]
    public void ResetDynamicRuntimeState_ClearsAllFields()
    {
        var grid = CreateGrid(new ClayGridOptions { Dynamic = true });
        // Заполняем поля ненулевыми значениями
        SetFld(grid, "_dynamicGridId", 101);
        SetFld(grid, "_dynamicClid", 1);
        SetFld(grid, "_dynamicDef", new ClayGridDefinition(101, "T", null, "S", null, null, null, null, null, false));
        SetFld(grid, "_dynamicCols", new List<ClayColumnDefinition> { new(1, 101, "C", "H", null, 1, null, 2, false) });
        SetFld(grid, "_dynamicKnownColumns", new HashSet<string> { "C" });
        SetFld(grid, "_dynamicLookups", new Dictionary<string, IReadOnlyDictionary<string, string>> { ["k"] = new Dictionary<string, string>() });
        SetFld(grid, "_dynamicError", "error");
        SetFld(grid, "_dynamicEditUrl", "/edit");
        SetFld(grid, "_dynamicSavedParams", new Dictionary<string, string> { ["p"] = "v" });
        SetFld(grid, "_searchText", "search");
        SetFld(grid, "_sortState", new List<SortColumn> { new("C", true) });
        SetFld(grid, "_filterRoot", new ClayFilterGroupNode());
        SetFld(grid, "_selectedIds", new HashSet<int> { 1, 2, 3 });
        SetFld(grid, "_selectMode", true);
        SetFld(grid, "_selectAllChecked", true);
        SetFld(grid, "_pageNumber", 5);

        CallReset(grid);

        // Всё очищено
        Assert.Equal(0, GetFld<int>(grid, "_dynamicGridId"));
        Assert.Null(GetFld<object>(grid, "_dynamicDef"));
        Assert.Empty(GetFld<IReadOnlyList<ClayColumnDefinition>>(grid, "_dynamicCols") ?? []);
        Assert.Empty(GetFld<HashSet<string>>(grid, "_dynamicKnownColumns") ?? []);
        Assert.Empty(GetFld<Dictionary<string, IReadOnlyDictionary<string, string>>>(grid, "_dynamicLookups") ?? []);
        Assert.Null(GetFld<string?>(grid, "_dynamicError"));
        Assert.Null(GetFld<string?>(grid, "_dynamicEditUrl"));
        Assert.Empty(GetFld<Dictionary<string, string>>(grid, "_dynamicSavedParams") ?? []);
        Assert.Null(GetFld<string>(grid, "_searchText"));
        Assert.Empty(GetFld<List<SortColumn>>(grid, "_sortState") ?? []);
        Assert.Empty(GetFld<HashSet<int>>(grid, "_selectedIds") ?? []);
        Assert.False(GetFld<bool>(grid, "_selectMode"));
        Assert.False(GetFld<bool>(grid, "_selectAllChecked"));
        Assert.Equal(1, GetFld<int>(grid, "_pageNumber"));
    }

    [Fact]
    public async Task DynamicToStatic_ResetsKey()
    {
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var grid = CreateGrid(new ClayGridOptions { Dynamic = true, DynamicGridId = 101 });
        SetFld(grid, "_currentDynamicKey", ClayGridDynamicKey.Create(101, 1, null, _settings));

        // Переключаем в static
        var opt = GetFld<ClayGridOptions>(grid, "_opt");
        Assert.NotNull(opt);
        opt!.Dynamic = false;

        await CallOnParamsSetAsync(grid);
        Assert.Null(GetFld<ClayGridDynamicKey?>(grid, "_currentDynamicKey"));
    }

    [Fact]
    public void Query_CacheInvalidatesOnUriChange()
    {
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var grid = CreateGrid(new ClayGridOptions { Dynamic = true });

        // Читаем Query через reflection
        var query1 = AccessQuery(grid);
        Assert.Equal("101", query1["id"]);

        // Меняем URI
        _nav.NavigateTo("http://localhost/?id=202&CLID=2");
        var query2 = AccessQuery(grid);
        Assert.Equal("202", query2["id"]);
    }

    [Fact]
    public void Query_SameUri_ReturnsSameInstance()
    {
        _nav.NavigateTo("http://localhost/?id=101&CLID=1");
        var grid = CreateGrid(new ClayGridOptions { Dynamic = true });

        var q1 = AccessQuery(grid);
        var q2 = AccessQuery(grid);
        Assert.Same(q1, q2);
    }

    private static NameValueCollection AccessQuery(object instance)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var t = instance.GetType();
        var prop = t.GetProperty("Query", flags);
        return (NameValueCollection)prop!.GetValue(instance)!;
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
