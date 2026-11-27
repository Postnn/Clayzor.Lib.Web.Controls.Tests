using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет перенос прежней синхронизации с проверкой почтовой политики.</summary>
public sealed class SlBlLegacyMigrationTests
{
    /// <summary>Сервер учитывает прежний GUID без отправки почтовой команды или записи адреса в журнал.</summary>
    [Fact]
    public async Task ImportRecordsOnlyOpaqueAuthorizedScope()
    {
        DapperColumnMapper.Initialize();
        const string address = "operator@example.invalid";
        var script = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4))),
            Script.Rows(Script.Row(("Address", address))),
            Script.Rows(Script.Row(("Available", true))),
            Script.Rows(),
            Script.Rows(Script.Row(("Owned", true))),
        ]), emulateEquipmentIdentity: false);
        var connection = new EquipmentAccountTransactionConnection(script);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var adapter = new SlBlSqlSynchronization(db, "fixture", new Policy(address));
        var id = Guid.NewGuid();

        await adapter.ImportLegacyOperationsAsync(address, SlBlKind.Safe, [id], default);

        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
        Assert.Equal(SQLQueries.INSERT_ClayzorLegacyOperationEvidence, script.CommandLog[^1]);
        var parameters = script.ParameterLog[^1];
        Assert.Equal(id, parameters["AttemptId"]);
        var scope = Assert.IsType<string>(parameters["ConflictKey"]);
        Assert.StartsWith("slbl/", scope);
        Assert.DoesNotContain(address, scope);
        Assert.Equal("slbl.sync", parameters["CommandKind"]);
        Assert.DoesNotContain(SQLQueries.SP_SlBlSynchronize, script.CommandLog);
    }

    private sealed class Policy(string address) : ISlBlMailboxWritePolicy
    {
        public Task<bool> AllowsAsync(Employee actor, string candidate, SlBlKind kind, CancellationToken ct) =>
            Task.FromResult(actor.Id == 4 && candidate == address && kind == SlBlKind.Safe);
    }
}
