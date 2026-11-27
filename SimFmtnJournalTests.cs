using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет точное подтверждение строкового ключа FMTN.</summary>
public sealed class SimFmtnJournalTests
{
    private const string Prefix = "00000000001";
    private static readonly SimFmtnFields Fields = new(Prefix, "5101", "fixture");

    /// <summary>Вставка, строковый результат и подтверждение используют один DbTransaction.</summary>
    [Fact]
    public async Task InsertAndTextReceiptUseTheSameTransaction()
    {
        var (script, connection, db) = Create([
            .. ActorAndAccess(), Script.Rows(), Script.Rows(),
            Script.Rows(), ClearSlot(), ClearSlot(), Script.NonQuery(), Script.Scalar(1),
            .. ActorAndAccess(), Script.Rows(), Script.NonQuery(), SavedRow(), Script.Scalar(1), Script.Scalar(1), Script.Scalar(1),
        ]);
        using (db)
        {
            var id = Guid.NewGuid();
            var saved = await SimFmtnData.SaveJournaledAsync(db, "fixture", 4, null, Fields, id, default);
            Assert.Equal(Prefix, saved.Prefix);
            var write = script.CommandLog.IndexOf(SQLQueries.INSERT_SimFmtn);
            var textProof = script.CommandLog.IndexOf(SQLQueries.INSERT_ClayzorOperationTextReceipt);
            var confirmation = script.CommandLog.IndexOf(SQLQueries.UPDATE_ClayzorOperationJournalState);
            Assert.True(write >= 0 && write < textProof && textProof < confirmation);
            Assert.Same(connection.Commands[write].Transaction, connection.Commands[textProof].Transaction);
            Assert.Same(connection.Commands[write].Transaction, connection.Commands[confirmation].Transaction);
            Assert.NotNull(connection.Commands[write].Transaction);
            Assert.Equal(id, script.ParameterLog[textProof]["AttemptId"]);
            Assert.Equal(Prefix, script.ParameterLog[textProof]["ResultKey"]);
        }
    }

    /// <summary>Повтор подтверждённой попытки читает точный строковый ключ и не вставляет запись.</summary>
    [Fact]
    public async Task ConfirmedReplayReadsTheTextReceiptAndDoesNotInsert()
    {
        var id = Guid.NewGuid();
        var (script, connection, db) = Create([
            .. ActorAndAccess(), Confirmed(id), Script.Rows(Script.Row(("ConflictKey", $"sim-fmtn/{Prefix}"))),
            Script.Rows(Script.Row(("ResultKey", Prefix))), SavedRow(),
        ]);
        using (db)
        {
            Assert.Equal(Prefix, (await SimFmtnData.SaveJournaledAsync(db, "fixture", 4, null, Fields, id, default)).Prefix);
            Assert.Contains(SQLQueries.SELECT_ClayzorOperationTextReceipt, script.CommandLog);
            Assert.DoesNotContain(SQLQueries.INSERT_SimFmtn, script.CommandLog);
            Assert.Empty(connection.TransactionLog);
        }
    }

    /// <summary>Прежняя смена ключа удерживает источник и цель до сверки, не изменяя данные FMTN.</summary>
    [Fact]
    public async Task LegacyRenameImportsBothKeysWithoutChangingRows()
    {
        const string oldPrefix = "00000000002";
        var (script, connection, db) = Create([
            .. ActorAndAccess(), Script.Rows(), Script.Rows(Script.Row(("Owned", true))), Script.Scalar(1),
        ]);
        using (db)
        {
            var id = Guid.NewGuid();
            await SimFmtnData.ImportLegacyOperationsAsync(db, "fixture", 4,
                [(id, oldPrefix, Prefix, false)], default);

            var source = script.CommandLog.IndexOf(SQLQueries.INSERT_ClayzorLegacyOperationEvidence);
            var target = script.CommandLog.IndexOf(SQLQueries.INSERT_ClayzorLegacyOperationScope);
            Assert.True(source >= 0 && source < target);
            Assert.Equal($"sim-fmtn/{oldPrefix}", script.ParameterLog[source]["ConflictKey"]);
            Assert.Equal($"sim-fmtn/{Prefix}", script.ParameterLog[target]["ConflictKey"]);
            Assert.Equal(id, script.ParameterLog[target]["AttemptId"]);
            Assert.Same(connection.Commands[source].Transaction, connection.Commands[target].Transaction);
            Assert.DoesNotContain(SQLQueries.UPDATE_SimFmtn, script.CommandLog);
        }
    }

    private static ScriptEntry[] ActorAndAccess() =>
    [
        Script.Rows(Script.Row(("КодСотрудника", 4))),
        Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))),
        Script.Rows(Script.Row(("CanRead", true), ("CanInsert", true), ("CanUpdate", true), ("CanDelete", true))),
    ];

    private static ScriptEntry ClearSlot() => Script.Rows(Script.Row(("Pending", false)));
    private static ScriptEntry SavedRow() =>
        Script.Rows(Script.Row(("Prefix", Prefix), ("ShortNumber", Fields.ShortNumber), ("Description", Fields.Description)));

    private static ScriptEntry Confirmed(Guid attemptId) => new()
    {
        ReaderFactory = () =>
        {
            var table = new DataTable();
            table.Columns.Add("AttemptId", typeof(Guid));
            table.Columns.Add("EmployeeId", typeof(int));
            table.Columns.Add("ConflictKey", typeof(string));
            table.Columns.Add("CommandKind", typeof(string));
            table.Columns.Add("State", typeof(byte));
            table.Columns.Add("ResultEntityId", typeof(int));
            table.Rows.Add(attemptId, 4, "sim-fmtn/new", "sim-fmtn.insert", (byte)InventoryOperationState.Confirmed, DBNull.Value);
            return table.CreateDataReader();
        },
    };

    private static (ScriptedConnection Script, EquipmentAccountTransactionConnection Connection, DbManager Db) Create(
        ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var connection = new EquipmentAccountTransactionConnection(script);
        return (script, connection, new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection));
    }
}
