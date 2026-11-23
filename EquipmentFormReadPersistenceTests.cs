using Clayzor.Lib.DALC;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки различения not-found и ошибок первичного чтения оборудования.</summary>
public sealed class EquipmentFormReadPersistenceTests
{
    /// <summary>Корректный пустой SELECT означает отсутствие записи, а не SQL-ошибку.</summary>
    [Fact]
    public async Task GetAsync_NoRows_ReturnsNull()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        Assert.Null(await service.GetAsync(999));
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Connectivity при первом SELECT не превращается в пустой результат.</summary>
    [Theory]
    [InlineData(53)]
    [InlineData(-2)]
    public async Task GetAsync_Connectivity_Throws(int errorNumber)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Error(SqlExceptionFactory.Create(errorNumber, "connection error")),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => service.GetAsync(7));
        Assert.Single(connection.CommandLog);
    }
}
