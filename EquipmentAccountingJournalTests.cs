using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет исходы журнала бухучёта без запуска SQL Server.</summary>
public sealed class EquipmentAccountingJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private const int OwnerId = 19;
    private const int AccountingId = 41;
    private static readonly DateTime ChangedAt = new(2026, 11, 27, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task InsertMaterialConfirmsExactKeyAndDoesNotWriteHiddenFields()
    {
        using var fixture = Create([.. AccountingAccess(), .. AccountingAccess(), Script.Scalar(1),
            Proof(0, AccountingId, null, OwnerId), Script.Scalar(1)]);
        var accounting = Accounting(0);
        accounting.IsMaterial = true;
        accounting.TenantId = 23;
        accounting.InventoryNumber = "Скрытый номер 01";
        var attemptId = Guid.NewGuid();

        await fixture.Service.SaveAccountingAsync(accounting, EmployeeId, attemptId, default);

        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        var parameters = EnlistedParameters(fixture);
        Assert.Equal(EquipmentId, parameters["КодОборудования"]);
        Assert.Equal(OwnerId, parameters["КодЛица"]);
        Assert.True(Assert.IsType<bool>(parameters["Материал"]));
        Assert.True(parameters["КодЛицаАрендатора"] is null or DBNull);
        Assert.Equal(string.Empty, parameters["ИнвентарныйНомер"]);
        Assert.Equal(2, fixture.Connection.CommandLog.Count(sql => sql == SQLQueries.SELECT_ТекущийСотрудникПоPrincipal));
        Assert.Equal(2, fixture.Connection.CommandLog.Count(sql => sql == SQLQueries.SELECT_ТекущиеРолиДляСотрудника));
        Assert.Single(fixture.Connection.CommandLog, sql => sql == SQLQueries.INSERT_ClayzorOperationTextReceipt);
        Fixture.EnqueueReceiptRead(fixture.Entries, fixture.Connection, OwnerId);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, AccountingId),
            await fixture.Service.ReadAccountingWriteReceiptAsync(EquipmentId, 0,
                EquipmentFormService.InsertAccountingCommand, EmployeeId, attemptId, default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task UpdateVersionOrDocumentRefusalDoesNotConfirm(int status)
    {
        using var fixture = Create([.. AccountingAccess(), .. AccountingAccess(), Script.Scalar(1), Proof(status, null, null, null)]);
        var attemptId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveAccountingAsync(Accounting(AccountingId), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.Equal(ChangedAt, EnlistedParameters(fixture)["ExpectedChangedAt"]);
        Assert.DoesNotContain(SQLQueries.INSERT_ClayzorOperationTextReceipt, fixture.Connection.CommandLog);
    }

    [Fact]
    public async Task DeleteRejectsWhenAnotherAccountingRowBecameLatest()
    {
        using var fixture = Create([.. AccountingAccess(), .. AccountingAccess(), Proof(2, null, null, null)]);
        var attemptId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.DeleteAccountingAsync(EquipmentId, AccountingId, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.Equal(AccountingId, EnlistedParameters(fixture)["КодОборудованияБухУчета"]);
        Assert.DoesNotContain(SQLQueries.INSERT_ClayzorOperationTextReceipt, fixture.Connection.CommandLog);
    }

    [Fact]
    public async Task InsertWithRevokedOwnerRoleIsRejectedBeforeDml()
    {
        using var fixture = Create([.. AccountingAccess(), Actor(), Roles(OwnerId + 1)]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveAccountingAsync(Accounting(0), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Connection.CommandLog, IsAccountingWrite);
    }

    [Fact]
    public async Task UpdatedReceiptRequiresBothOwnersFromServerProof()
    {
        const int newOwner = 21;
        var roles = Script.Rows(Script.Row(("RoleId", (int)Роли.Бухгалтер), ("PersonId", OwnerId)),
            Script.Row(("RoleId", (int)Роли.Главный_бухгалтер_1С), ("PersonId", newOwner)));
        using var fixture = Create([Actor(), roles, Actor(), roles, Script.Scalar(1),
            Proof(0, AccountingId, OwnerId, newOwner), Script.Scalar(1)]);
        var value = Accounting(AccountingId);
        value.OwnerId = newOwner;
        var attemptId = Guid.NewGuid();
        await fixture.Service.SaveAccountingAsync(value, EmployeeId, attemptId, default);

        Fixture.EnqueueReceiptRead(fixture.Entries, fixture.Connection, newOwner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ReadAccountingWriteReceiptAsync(
            EquipmentId, AccountingId, EquipmentFormService.UpdateAccountingCommand, EmployeeId, attemptId, default));

        Fixture.EnqueueReceiptRead(fixture.Entries, fixture.Connection, personId: 0);
        fixture.Entries.Enqueue(Script.Scalar(1));
        var receipt = await fixture.Service.ReadAccountingWriteReceiptAsync(EquipmentId, AccountingId,
            EquipmentFormService.UpdateAccountingCommand, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, AccountingId), receipt);
        Assert.Equal($"{OwnerId}/{newOwner}", fixture.Connection.ParameterLog[
            fixture.Connection.CommandLog.IndexOf(SQLQueries.INSERT_ClayzorOperationTextReceipt)]["ResultKey"]);
        Assert.Single(fixture.Connection.CommandLog, IsAccountingWrite);
    }

    [Fact]
    public async Task LostDeleteCommitRequiresCurrentOwnerAccessForExactReadAndDoesNotDeleteAgain()
    {
        using var fixture = Create([.. AccountingAccess(), .. AccountingAccess(), Proof(0, AccountingId, OwnerId, null), Script.Scalar(1)]);
        var attemptId = Guid.NewGuid();
        fixture.Journal.LoseNextConfirmedCommitReply = true;
        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.DeleteAccountingAsync(EquipmentId, AccountingId, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        Fixture.EnqueueReceiptRead(fixture.Entries, fixture.Connection, OwnerId + 1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.ReadAccountingWriteReceiptAsync(EquipmentId, AccountingId, EquipmentFormService.DeleteAccountingCommand,
                EmployeeId, attemptId, default));

        Fixture.EnqueueReceiptRead(fixture.Entries, fixture.Connection, OwnerId);
        var receipt = await fixture.Service.ReadAccountingWriteReceiptAsync(EquipmentId, AccountingId,
            EquipmentFormService.DeleteAccountingCommand, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, AccountingId), receipt);
        foreach (var entry in AccountingAccess()) fixture.Entries.Enqueue(entry);
        Fixture.EnqueueReceiptRead(fixture.Entries, fixture.Connection, OwnerId);
        await fixture.Service.DeleteAccountingAsync(EquipmentId, AccountingId, EmployeeId, attemptId, default);
        Assert.DoesNotContain(SQLQueries.SELECT_ОборудованиеБухУчета_КодОборудования, fixture.Connection.CommandLog);
        Assert.Single(fixture.Connection.CommandLog, IsAccountingWrite);
        Assert.Single(fixture.Connection.CommandLog, sql => sql == SQLQueries.INSERT_ClayzorOperationTextReceipt);
    }

    private static bool IsAccountingWrite(string sql) => sql.Contains("ConfirmedAccountingId", StringComparison.Ordinal) &&
        sql.Contains("ОборудованиеБухУчета", StringComparison.Ordinal);

    private static Dictionary<string, object?> EnlistedParameters(Fixture fixture)
    {
        var statement = Assert.Single(fixture.Connection.CommandLog, IsAccountingWrite);
        return fixture.Connection.ParameterLog[fixture.Connection.CommandLog.IndexOf(statement)];
    }

    private static EquipmentAccounting Accounting(int id) => new()
    {
        Id = id, EquipmentId = EquipmentId, OwnerId = OwnerId, InventoryNumber = "INV0127",
        DateFrom = ChangedAt.AddDays(-1), ChangedAt = ChangedAt,
    };

    private static ScriptEntry Actor() => Script.Rows(Script.Row(("КодСотрудника", EmployeeId)));
    private static ScriptEntry Roles(int personId) => Script.Rows(Script.Row(("RoleId", (int)Роли.Бухгалтер), ("PersonId", personId)));
    private static ScriptEntry RootEquipment() => Script.Rows(Script.Row(("КодОборудования", EquipmentId)));
    private static ScriptEntry[] AccountingAccess() => [Actor(), Roles(OwnerId)];

    private static ScriptEntry Proof(int status, int? id, int? originalOwner, int? effectiveOwner) => new()
    {
        ReaderFactory = () =>
        {
            var table = new DataTable();
            table.Columns.Add("Status", typeof(int));
            table.Columns.Add("ConfirmedAccountingId", typeof(int));
            table.Columns.Add("OriginalOwnerId", typeof(int));
            table.Columns.Add("EffectiveOwnerId", typeof(int));
            table.Rows.Add(status, (object?)id ?? DBNull.Value,
                (object?)originalOwner ?? DBNull.Value, (object?)effectiveOwner ?? DBNull.Value);
            return table.CreateDataReader();
        },
    };

    private static Fixture Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var queue = new Queue<ScriptEntry>(entries);
        var script = new ScriptedConnection(queue, emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(script);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => journal);
        return new Fixture(new EquipmentFormService(db, new TestAuthenticationStateProvider()), script, journal, queue, db);
    }

    private sealed record Fixture(EquipmentFormService Service, ScriptedConnection Connection,
        NetFormTests.NetJournalConnection Journal, Queue<ScriptEntry> Entries, DbManager Db) : IDisposable
    {
        internal static void EnqueueReceiptRead(Queue<ScriptEntry> entries, ScriptedConnection script, int personId)
        {
            entries.Enqueue(Actor());
            entries.Enqueue(Roles(personId));
            entries.Enqueue(RootEquipment());
            // Echo the recorded text argument. The generic Net fixture models the committed attempt;
            // this entry models only the read of its accompanying text metadata, not an actual SQL transaction.
            entries.Enqueue(new ScriptEntry
            {
                ReaderFactory = () =>
                {
                    var insert = script.CommandLog.IndexOf(SQLQueries.INSERT_ClayzorOperationTextReceipt);
                    var metadata = Assert.IsType<string>(script.ParameterLog[insert]["ResultKey"]);
                    var table = new DataTable();
                    table.Columns.Add("ResultKey", typeof(string));
                    table.Rows.Add(metadata);
                    return table.CreateDataReader();
                },
            });
            if (personId == OwnerId || personId == 0)
                entries.Enqueue(Script.Scalar(1));
        }

        public void Dispose() => Db.Dispose();
    }
}
