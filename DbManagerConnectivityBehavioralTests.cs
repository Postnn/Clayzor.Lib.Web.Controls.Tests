using System.Data;
using Clayzor.Lib.DALC;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Behavioral tests контракта DALC connectivity (CTFR3.1).
/// Использует гарантированно-плохой connection string для connectivity-сценариев.
/// Non-connectivity/cancellation внутри action недоступны без реального SQL Server —
/// Connection.Open() происходит первым и бросает connectivity SqlException.
/// Классификация ошибок покрыта в DbManagerConnectivityTests.
/// </summary>
public class DbManagerConnectivityBehavioralTests
{
    private sealed class CountingHandler : ISqlErrorHandler
    {
        public int CallCount { get; private set; }

        public void HandleSqlError(SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters)
        {
            CallCount++;
        }
    }

    /// <summary>Закрытый порт — гарантированный connectivity error.</summary>
    private const string BadConnString = "Server=127.0.0.1,59999;Database=x;Connect Timeout=2;Pooling=false";

    private static DbManager CreateDb(CountingHandler handler)
        => new(BadConnString, handler);

    // ═══ RunAsync — connectivity ═══

    [Fact]
    public async Task RunAsync_Connectivity_HandlerCalledOnceAndThrows()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        await Assert.ThrowsAsync<SqlException>(() =>
            db.RunAsync<int>(c => c.QuerySingleAsync<int>("SELECT 1")));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunAsync_Connectivity_GateReleasedAfterException()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        try { await db.RunAsync<int>(c => c.QuerySingleAsync<int>("SELECT 1")); }
        catch (SqlException) { }

        // Gate свободен — следующий RunAsync выполняется (снова connectivity, но без deadlock)
        await Assert.ThrowsAsync<SqlException>(() =>
            db.RunAsync<int>(c => c.QuerySingleAsync<int>("SELECT 1")));

        Assert.Equal(2, handler.CallCount); // 2 вызова = gate работает
    }

    // ═══ ExecuteAsync (write) ═══

    [Fact]
    public async Task ExecuteAsync_Connectivity_ThrowsNotZero()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecuteAsync("DELETE FROM t", new { id = 1 }, CommandType.Text));

        Assert.Equal(1, handler.CallCount);
        Assert.True(DbManager.IsConnectivityError(ex));
    }

    [Fact]
    public async Task ExecuteAsync_Connectivity_HandlerCalledExactlyOnce()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        try { await db.ExecuteAsync("DELETE FROM t", new { id = 1 }, CommandType.Text); }
        catch (SqlException) { }

        // CTFR3: handler 1 раз (в RunAsync), не 2 (не в outer catch)
        Assert.Equal(1, handler.CallCount);
    }

    // ═══ Read tests ═══

    [Fact]
    public async Task QueryAsync_Connectivity_ReturnsEmpty()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        var result = await db.QueryAsync<int>("SELECT 1");
        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ExecuteScalarAsync_Connectivity_ReturnsDefault()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        var result = await db.ExecuteScalarAsync<int>("SELECT 1", null, CommandType.Text);
        Assert.Equal(0, result); // default(int)
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task QueryStoredProcAsync_Connectivity_ReturnsEmpty()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        var result = await db.QueryStoredProcAsync<int>("sp_test");
        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ExecuteScalarAsync_Connectivity_HandlerCalledOnce()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        await db.ExecuteScalarAsync<int>("SELECT 1", null, CommandType.Text);
        Assert.Equal(1, handler.CallCount);
    }

    // ═══ DynamicSql read tests ═══

    [Fact]
    public async Task DynamicSql_QueryRowsAsync_Connectivity_ReturnsEmpty_HandlerOnce()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        var result = await Clayzor.Lib.Entities.DynamicGrid.DynamicSql.QueryRowsAsync(db, "SELECT 1");
        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DynamicSql_QueryCountAsync_Connectivity_ReturnsZero_HandlerOnce()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        var result = await Clayzor.Lib.Entities.DynamicGrid.DynamicSql.QueryCountAsync(db, "SELECT 1", null);
        Assert.Equal(0, result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DynamicSql_ExecuteAsync_Connectivity_Throws_HandlerOnce()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler);

        await Assert.ThrowsAsync<SqlException>(() =>
            Clayzor.Lib.Entities.DynamicGrid.DynamicSql.ExecuteAsync(db, "DELETE FROM t"));
        Assert.Equal(1, handler.CallCount);
    }
}
