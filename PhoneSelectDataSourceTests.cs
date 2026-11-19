using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Phones;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки SQL, параметров и Limit+1 источников формы телефона.</summary>
public sealed class PhoneSelectDataSourceTests
{
    /// <summary>Тип фильтруется станцией и возвращает Limit+1 для HasMore.</summary>
    [Fact]
    public async Task PhoneType_UsesStationFilterAndLimitPlusOne()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодТипаТелефонныхНомеров", 1), ("ТипТелефонныхНомеров", "Digital"), ("ТипТелефонныхНомеровЛат", "Digit")),
                Script.Row(("КодТипаТелефонныхНомеров", 2), ("ТипТелефонныхНомеров", "Analog"), ("ТипТелефонныхНомеровЛат", "Analog"))),
        ]));
        var source = new PhoneTypeSelectDataSource(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection)) { StationId = 32 };
        var result = await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "a", Limit = 1 });
        Assert.True(result.HasMore);
        Assert.Single(result.Items);
        Assert.Equal(2, connection.ParameterLog[0]["LimitPlusOne"]);
        Assert.Equal(32, connection.ParameterLog[0]["StationId"]);
        Assert.Contains("S.DECT = 1", connection.CommandLog[0]);
    }

    /// <summary>Ведущие и отображаемые используют разные условия и безопасные исключения.</summary>
    [Theory]
    [InlineData(PhoneDirectoryMode.DrivenBy, "P.ВедомыйОт IS NULL")]
    [InlineData(PhoneDirectoryMode.DisplayedFrom, "P.ОтображаемыйОт IS NULL")]
    public async Task Directory_KeepsModeSpecificFilters(PhoneDirectoryMode mode, string expected)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new PhoneDirectorySelectDataSource(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection))
        { Mode = mode, StationId = 1, ExcludedIds = [5, 8] };
        await source.SearchAsync(new ClaySelectSearchRequest { Limit = 20 });
        Assert.Contains(expected, connection.CommandLog[0]);
        Assert.Contains("P.КодТелефонногоНомера <> @Excluded0", connection.CommandLog[0]);
        Assert.Equal(5, connection.ParameterLog[0]["Excluded0"]);
        Assert.Equal(8, connection.ParameterLog[0]["Excluded1"]);
    }

    /// <summary>Составной ключ оборудования восстанавливается без строки с разделителем.</summary>
    [Fact]
    public async Task Equipment_UsesTypedCompositeKey()
    {
        global::Clayzor.Lib.Entities.MedicalTests.DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодОборудованияПодключения", 9), ("Название", "Phone"))),
        ]));
        var source = new PhoneEquipmentSelectDataSource(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        var item = await source.GetByIdAsync(new PhoneEquipmentKey(7, 9));
        Assert.Equal(new PhoneEquipmentKey(7, 9), item!.Key);
        Assert.Equal(7, connection.ParameterLog[0]["EquipmentId"]);
        Assert.Equal(9, connection.ParameterLog[0]["ConnectionEquipmentId"]);
    }

    /// <summary>Без офиса станции розетки не открываются глобальным поиском.</summary>
    [Fact]
    public async Task Socket_MissingContextFailsClosed()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([new ScriptEntry()]));
        var source = new PhoneSocketSelectDataSource(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection)) { StationId = 1 };
        var result = await source.SearchAsync(new ClaySelectSearchRequest { Limit = 20 });
        Assert.Empty(result.Items);
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Черновые расположения оборудования передаются параметрами.</summary>
    [Fact]
    public async Task Socket_UsesDraftEquipmentLocations()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new PhoneSocketSelectDataSource(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection)) { EquipmentLocationIds = [10, 20] };
        await source.SearchAsync(new ClaySelectSearchRequest { Limit = 20 });
        Assert.Contains("IN (@LocationId0,@LocationId1)", connection.CommandLog[0]);
        Assert.Equal(10, connection.ParameterLog[0]["LocationId0"]);
        Assert.Equal(20, connection.ParameterLog[0]["LocationId1"]);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("ОборудованиеРасположение R ON R.КодОборудования = S.КодОборудования"));
    }
}
