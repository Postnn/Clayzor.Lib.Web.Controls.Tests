using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Locations;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки специфических legacy-фильтров розеток формы Net.</summary>
public sealed class NetSocketSelectDataSourceTests
{
    /// <summary>Список включает текущую занятую розетку, исключает обе занятости и ограничивается деревом офиса.</summary>
    [Fact]
    public async Task Search_UsesAliveFreeCurrentAndOfficeTreeFilters()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодРозетки", 5), ("Розетка", "A-1"), ("Примечание", "note"),
                ("КодРасположения", 55), ("Работает", true))),
        ]));
        var source = new NetSocketSelectDataSource(CreateDb(connection))
        {
            CurrentSocketId = 5,
            OfficeLocationId = 50,
        };

        var result = await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "A", Limit = 8 });

        Assert.Single(result.Items);
        Assert.Equal("note", result.Items[0].Notes);
        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("S.Работает = 1", sql);
        Assert.Contains("vwТелефонныеНомераИзменение", sql);
        Assert.Contains("vwЛокальнаяСеть", sql);
        Assert.Contains("S.КодРозетки = @CurrentSocketId", sql);
        Assert.Contains("Parent.L <= Child.L", sql);
        Assert.Equal(5, connection.ParameterLog[0]["CurrentSocketId"]);
        Assert.Equal(50, connection.ParameterLog[0]["OfficeLocationId"]);
    }

    /// <summary>При отсутствии офиса ID порта не подставляется в фильтр расположения.</summary>
    [Fact]
    public async Task Search_WithoutOffice_DoesNotUsePortIdAsLocation()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new NetSocketSelectDataSource(CreateDb(connection));

        await source.SearchAsync(new ClaySelectSearchRequest { Limit = 8 });

        Assert.Equal(DBNull.Value, connection.ParameterLog[0]["OfficeLocationId"]);
        Assert.DoesNotContain("CurrentPort", connection.CommandLog[0]);
    }

    /// <summary>Текущая розетка восстанавливается по ID с примечанием независимо от занятости.</summary>
    [Fact]
    public async Task GetById_LoadsNotesWithoutFreeFilter()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодРозетки", 5), ("Розетка", "A-1"), ("Примечание", "occupied"),
                ("КодРасположения", 55), ("Работает", true))),
        ]));
        var source = new NetSocketSelectDataSource(CreateDb(connection));

        var socket = await source.GetByIdAsync(5);

        Assert.Equal("occupied", socket!.Notes);
        Assert.DoesNotContain("vwТелефонныеНомераИзменение", Assert.Single(connection.CommandLog));
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
