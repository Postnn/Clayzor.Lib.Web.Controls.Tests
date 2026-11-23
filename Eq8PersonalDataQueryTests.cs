using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки источника лиц ПДн SIM EQ8-07B против pinned legacy.</summary>
public sealed class Eq8PersonalDataQueryTests
{
    /// <summary>PersonalData использует активные технические тарифы из CTE и передаёт полный контекст.</summary>
    [Fact]
    public async Task PersonalDataSearch_RestoresActiveTechnicalTariffCte()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new PersonalDataPersonSelectDataSource(CreateDb(connection))
        {
            EquipmentId = 7,
            ContractId = 10,
            CustomerType = 2,
            TechnicalTariffs = "TECH,IOT",
        };

        await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "person", Limit = 8 });

        var sql = Normalize(Assert.Single(connection.CommandLog));
        const string expectedCte =
            "SIMКартыДопУслуги(НомерТелефона) AS ( SELECT U.НомерТелефона " +
            "FROM vwSIMКартыДопУслугиСотОператора U WHERE EXISTS (SELECT 1 FROM ТарифныеПланы T " +
            "WHERE U.КодТипаДопУслугиСотОператора LIKE T.КодТипаДопУслугиСотОператора + '%') " +
            "AND U.От < GETDATE() AND (U.До IS NULL OR U.До > GETDATE()) )";
        var cteIndex = sql.IndexOf(expectedCte, StringComparison.Ordinal);
        var employeesIndex = sql.IndexOf("СотрудникиНаДолжности(КодЛица, Сотрудник)", StringComparison.Ordinal);
        var useIndex = sql.IndexOf(
            "EXISTS (SELECT 1 FROM SIMКартыДопУслуги U WHERE U.НомерТелефона = S.НомерТелефона)",
            StringComparison.Ordinal);
        Assert.True(cteIndex > 0);
        Assert.True(employeesIndex > cteIndex + expectedCte.Length);
        Assert.True(useIndex > employeesIndex);
        Assert.Contains("SELECT word FROM dbo.fn_SplitText(@TechnicalTariffs)", sql);
        Assert.Contains("@EquipmentId IS NULL OR S.КодОборудования = @EquipmentId", sql);
        Assert.Contains("S.КодДоговора = D.КодДоговора", sql);
        Assert.Contains("UNION SELECT КодЛицаЗаказчика, Заказчик, 0 FROM Договора WHERE @CustomerType = 2", sql);

        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal("person", parameters["Search"]);
        Assert.Equal(9, parameters["LimitPlusOne"]);
        Assert.Equal("TECH,IOT", parameters["TechnicalTariffs"]);
        Assert.Equal(10, parameters["ContractId"]);
        Assert.Equal(7, parameters["EquipmentId"]);
        Assert.Equal(2, parameters["CustomerType"]);
    }

    /// <summary>Оба contract query возвращают фактический vwЛица.ТипЛица, а не признак записи в Сотрудники.</summary>
    [Fact]
    public async Task ContractQueries_UseActualCustomerPersonType()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(), Script.Rows()]));
        var source = new MobileContractSelectDataSource(CreateDb(connection));

        await source.SearchAsync(new ClaySelectSearchRequest { SearchText = string.Empty, Limit = 8 });
        await source.GetByIdAsync(10);

        Assert.Equal(2, connection.CommandLog.Count);
        foreach (var command in connection.CommandLog)
        {
            var sql = Normalize(command);
            Assert.Contains("COALESCE((SELECT P.ТипЛица FROM vwЛица P WHERE P.КодЛица = ДоговорыСвязи.КодЛицаЗаказчика), 1) AS ТипЛицаЗаказчика", sql);
            Assert.DoesNotContain("EXISTS (SELECT 1 FROM Сотрудники E WHERE E.КодЛица = ДоговорыСвязи.КодЛицаЗаказчика)", sql);
        }
    }

    /// <summary>Connectivity SQL error не подменяется пустым списком кандидатов.</summary>
    [Fact]
    public async Task PersonalDataSearch_DoesNotConvertSqlErrorToEmptyResult()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Error(SqlExceptionFactory.Create(53, "connection error")),
        ]));
        var source = new PersonalDataPersonSelectDataSource(CreateDb(connection));

        await Assert.ThrowsAsync<SqlException>(() =>
            source.SearchAsync(new ClaySelectSearchRequest { SearchText = string.Empty, Limit = 8 }));
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
