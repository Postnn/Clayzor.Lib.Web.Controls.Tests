using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки пакетного SQL-резолвера ссылок порта.</summary>
public sealed class NetEquipmentLinkResolverTests
{
    /// <summary>R08/R12/R14/R20: пять предикатов, независимые поля, параметры и отсутствие N+1.</summary>
    [Fact]
    public async Task Resolve_BatchesFivePredicatesAndKeepsFieldsIndependent()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(
                Script.Row(("Token", "server01"), ("КодОборудования", 8)),
                Script.Row(("Token", "AA:BB:CC:DD:EE:FF"), ("КодОборудования", 7))),
        ]));
        var db = CreateDb(connection);

        var result = await NetEquipmentLinkResolver.ResolveAsync(db,
            "missing server01", "server01 AA:BB:CC:DD:EE:FF");

        Assert.Equal(8, Assert.Single(result.Connected).EquipmentId);
        Assert.Equal([7, 8], result.Description.Select(link => link.EquipmentId).OrderBy(id => id).ToArray());
        Assert.Equal(2, result.Description.Count);
        var sql = Assert.Single(connection.CommandLog);
        foreach (var predicate in new[] { "СетевоеИмя =", "CHARINDEX", "MACадрес =", "MACадрес2 =", "MACадресILO =" })
            Assert.Contains(predicate, sql);
        Assert.Contains("TOP 1", sql);
        Assert.DoesNotContain("ORDER BY", sql);
        Assert.DoesNotContain("missing server01", sql);
        Assert.Equal("missing", connection.ParameterLog[0]["Token0"]);
        Assert.Equal("server01", connection.ParameterLog[0]["Token1"]);
        Assert.Equal("AA:BB:CC:DD:EE:FF", connection.ParameterLog[0]["Token8"]);
    }

    /// <summary>R04: поиск и подпись используют преобразованный ilo-токен.</summary>
    [Fact]
    public async Task Resolve_IloToken_UsesTransformedValue()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Token", "server01"), ("КодОборудования", 8))),
        ]));
        var result = await NetEquipmentLinkResolver.ResolveAsync(CreateDb(connection), "ilo-server01", null);
        Assert.Equal("server01", Assert.Single(result.Connected).Token);
        Assert.Equal("server01", connection.ParameterLog[0]["Token0"]);
    }

    /// <summary>SQL-параметр 51-символьного токена сохраняет значение и фактический размер.</summary>
    [Fact]
    public async Task Resolve_LongToken_DoesNotTruncateParameter()
    {
        var token = new string('a', 51);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));

        await NetEquipmentLinkResolver.ResolveAsync(CreateDb(connection), token, null);

        Assert.Equal(token, connection.ParameterLog[0]["Token0"]);
        Assert.Equal(token.Length, connection.ParameterSizeLog[0]["Token0"]);
    }

    /// <summary>Длинный токен без совпадения не мешает короткой ссылке в другом поле.</summary>
    [Fact]
    public async Task Resolve_LongAndShortTokens_KeepFieldsIndependent()
    {
        var token = new string('a', 100);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Token", "server01"), ("КодОборудования", 8))),
        ]));

        var result = await NetEquipmentLinkResolver.ResolveAsync(CreateDb(connection), token, "server01");

        Assert.Empty(result.Connected);
        Assert.Equal(8, Assert.Single(result.Description).EquipmentId);
        Assert.Equal(token.Length, connection.ParameterSizeLog[0]["Token0"]);
    }

    /// <summary>R17: больше 500 токенов разбиваются на ограниченные пачки.</summary>
    [Fact]
    public async Task Resolve_LongText_UsesBoundedBatches()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(), Script.Rows()]));
        var text = string.Join(' ', Enumerable.Range(0, 501).Select(index => $"srv{index}"));

        var result = await NetEquipmentLinkResolver.ResolveAsync(CreateDb(connection), text, null);

        Assert.Empty(result.Connected);
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.Equal(500, connection.ParameterLog[0].Count);
        Assert.Single(connection.ParameterLog[1]);
    }

    /// <summary>R19/R20: отмена и SQL-ошибка не маскируются под отсутствие оборудования.</summary>
    [Fact]
    public async Task Resolve_SqlErrorAndCancellation_AreNotMissingEquipment()
    {
        var errorConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry { Exception = SqlExceptionFactory.Create(53, "connection error") },
        ]));
        await Assert.ThrowsAsync<SqlException>(() =>
            NetEquipmentLinkResolver.ResolveAsync(CreateDb(errorConnection), "server01", null));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NetEquipmentLinkResolver.ResolveAsync(CreateDb(new ScriptedConnection(new Queue<ScriptEntry>())), "server01", null, cts.Token));
    }

    private static DbManager CreateDb(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
    }
}
