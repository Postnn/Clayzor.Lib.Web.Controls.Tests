using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Граница между проверкой доступа и отправкой записи учётной записи оборудования.</summary>
public sealed class EquipmentAccountWritePhaseTests
{
    /// <summary>SQL ошибка чтения прав или типа оборудования возникает до команды записи.</summary>
    [Theory]
    [InlineData("add", 0)]
    [InlineData("update", 1)]
    [InlineData("delete", 2)]
    [InlineData("add", 2)]
    [InlineData("update", 2)]
    public async Task FailedPreflight_ReturnsNotSentWithoutExecutingWrite(string command, int successfulChecks)
    {
        var timeout = SqlExceptionFactory.Create(-2, "fixture read timeout");
        var entries = new Queue<ScriptEntry>(Enumerable.Repeat(Script.Scalar(1), successfulChecks));
        entries.Enqueue(Script.Error(timeout));
        using var connection = new ScriptedConnection(entries);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var service = new EquipmentFormService(db, new TestAuthenticationStateProvider());

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() => ExecuteAsync(service, command));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Same(timeout, error.InnerException);
        Assert.Contains("Команда не отправлена", error.Message);
        Assert.Equal(successfulChecks + 1, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT ОборудованиеУчётныеЗаписи")
            || sql.Contains("UPDATE ОборудованиеУчётныеЗаписи") || sql.Contains("DELETE ОборудованиеУчётныеЗаписи"));
    }

    /// <summary>Таймаут фактической записи сохраняет фазу Unknown и исключает SQL-параметры из ошибки.</summary>
    [Theory]
    [InlineData("add", "INSERT")]
    [InlineData("update", "UPDATE")]
    [InlineData("delete", "DELETE")]
    public async Task WriteTimeout_IsNotReclassifiedAsNotSent(string command, string sqlVerb)
    {
        var timeout = SqlExceptionFactory.Create(-2, "fixture write timeout");
        var entries = new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Scalar(1),
            Script.Rows(Script.Row((nameof(EquipmentType.MacAddressCount), (byte)1))),
        ]);
        if (command != "add")
            entries.Enqueue(EquipmentAccountCasTests.SnapshotRow(Original()));
        entries.Enqueue(Script.Error(timeout));

        using var connection = new ScriptedConnection(entries);
        using var transactional = new EquipmentAccountTransactionConnection(connection);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => transactional);
        var service = new EquipmentFormService(db, new TestAuthenticationStateProvider());

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() => ExecuteAsync(service, command));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.DoesNotContain("fixture write timeout", error.ToString());
        Assert.Equal(command == "add" ? 4 : 5, connection.CommandLog.Count);
        Assert.Contains(sqlVerb + " ОборудованиеУчётныеЗаписи", connection.CommandLog[^1]);
    }

    private static EquipmentAccount Original() => new() { EquipmentId = 7, Login = "fixture" };

    private static Task ExecuteAsync(EquipmentFormService service, string command)
    {
        var account = Original();
        account.ExpectedVersion = account.CaptureVersion();
        return command switch
        {
            "add" => service.AddAccountAsync(account),
            "update" => service.UpdateAccountAsync(account),
            "delete" => service.DeleteAccountAsync(account),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }
}
