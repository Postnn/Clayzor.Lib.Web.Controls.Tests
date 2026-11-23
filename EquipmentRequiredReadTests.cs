using Clayzor.Lib.DALC;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки fail-closed чтений, влияющих на снимки и последующие записи EquipmentForm.</summary>
public sealed class EquipmentRequiredReadTests
{
    /// <summary>Ошибка первого SELECT UPS не превращается в пустой список серверов.</summary>
    [Fact]
    public async Task UpsServersConnectivity_Throws() => await AssertServersError();

    /// <summary>Ошибка второго SELECT UPS не превращается в пустой список тестов.</summary>
    [Fact]
    public async Task UpsTestsConnectivity_Throws()
    {
        var (service, connection) = Create(SqlExceptionFactory.Create(10054, "connection lost"));
        await Assert.ThrowsAsync<SqlException>(() => service.GetUpsTestsAsync(15));
        Assert.Empty(connection.CommandLog.Where(sql => sql.Contains("INSERT") || sql.Contains("UPDATE") || sql.Contains("DELETE")));
    }

    /// <summary>Проверка дубликата SIM отличает connectivity от отсутствия дубликата.</summary>
    [Fact]
    public async Task DuplicateSimConnectivity_Throws()
    {
        var (service, connection) = Create(SqlExceptionFactory.Create(53, "connection error"));
        await Assert.ThrowsAsync<SqlException>(() => service.GetDuplicateSimIdAsync(9, "79000000001"));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE") || sql.Contains("DELETE"));
    }

    /// <summary>История бухгалтерии не очищается из-за connectivity.</summary>
    [Fact]
    public async Task AccountingConnectivity_Throws()
    {
        var (service, _) = Create(SqlExceptionFactory.Create(53, "connection error"));
        await Assert.ThrowsAsync<SqlException>(() => service.GetAccountingAsync(8));
    }

    /// <summary>Счётчик сети не считается нулевым при потере соединения.</summary>
    [Fact]
    public async Task NetworkCountConnectivity_Throws()
    {
        var (service, _) = Create(SqlExceptionFactory.Create(53, "connection error"));
        await Assert.ThrowsAsync<SqlException>(() => service.GetNetworkAttachmentCountAsync(8));
    }

    /// <summary>История расположений не очищается из-за connectivity.</summary>
    [Fact]
    public async Task LocationsConnectivity_Throws()
    {
        var (service, _) = Create(SqlExceptionFactory.Create(53, "connection error"));
        await Assert.ThrowsAsync<SqlException>(() => service.GetLocationsAsync(8));
    }

    /// <summary>История ответственных не очищается из-за connectivity.</summary>
    [Fact]
    public async Task ResponsiblesConnectivity_Throws()
    {
        var (service, _) = Create(SqlExceptionFactory.Create(53, "connection error"));
        await Assert.ThrowsAsync<SqlException>(() => service.GetResponsiblesAsync(8));
    }

    /// <summary>Нулевой результат без ошибки остаётся нормальным отсутствием дубликата.</summary>
    [Fact]
    public async Task DuplicateSimNoRows_ReturnsNull()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        Assert.Null(await service.GetDuplicateSimIdAsync(9, "79000000001"));
    }

    private static async Task AssertServersError()
    {
        var (service, connection) = Create(SqlExceptionFactory.Create(53, "connection error"));
        await Assert.ThrowsAsync<SqlException>(() => service.GetUpsServersAsync(15));
        Assert.Empty(connection.CommandLog.Where(sql => sql.Contains("INSERT") || sql.Contains("UPDATE") || sql.Contains("DELETE")));
    }

    private static (EquipmentFormService Service, ScriptedConnection Connection) Create(Exception exception)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([new ScriptEntry { Exception = exception }]));
        return (new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider()), connection);
    }
}
