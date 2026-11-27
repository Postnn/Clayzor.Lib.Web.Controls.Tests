using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;
using static Clayzor.Lib.Web.Controls.Tests.EquipmentSimConcurrencyTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет точный серверный исход и защиту полей SIM на пути записи.</summary>
public sealed class EquipmentSimJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private const int DuplicateId = 8;

    [Fact]
    public async Task LostAddCommitKeepsExactTargetAndDuplicateScopeAndDoesNotWriteAgain()
    {
        var sim = Source();
        var duplicate = Source(personId: 17);
        duplicate.EquipmentId = DuplicateId;
        var persisted = Source(personId: 17);
        using var fixture = Create([.. Access(), .. Access(), Duplicate(DuplicateId), ActualSimType(), SnapshotRow(duplicate),
            Script.Scalar(1), SnapshotRow(persisted),
            Script.Rows(Script.Row(("КодОборудования", DuplicateId), ("Списано", sim.ActivatedAtUtc))),
            .. Access(), .. Access(), .. Access()]);
        var attemptId = Guid.NewGuid();
        fixture.Journal.LoseNextConfirmedCommitReply = true;

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSimAsync(sim, EquipmentSimWriteMode.Add, null, DuplicateId, EmployeeId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, failure.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() => fixture.Service.ReadSimWriteReceiptAsync(
            EquipmentId, DuplicateId + 1, EquipmentFormService.InsertSimCommand, EmployeeId, attemptId, default));

        var receipt = await fixture.Service.ReadSimWriteReceiptAsync(EquipmentId, DuplicateId,
            EquipmentFormService.InsertSimCommand, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), receipt);
        Assert.Equal("equipment/8", Assert.Single(fixture.Journal.ScopesFor(attemptId)));
        await fixture.Service.SaveSimAsync(sim, EquipmentSimWriteMode.Add, null, DuplicateId, EmployeeId, attemptId, default);

        var sql = Assert.Single(fixture.Script.CommandLog, IsSimWrite);
        Assert.Contains("INSERT vwSIMКарты", sql);
        Assert.Contains("UPDATE vwОборудование", sql);
        Assert.DoesNotContain("BEGIN TRANSACTION", sql);
        Assert.DoesNotContain("COMMIT TRANSACTION", sql);
        var duplicateCheck = fixture.Script.CommandLog.IndexOf(SQLQueries.SELECT_АктивныеДубликатыSIM);
        Assert.True(duplicateCheck >= 0 && duplicateCheck < fixture.Script.CommandLog.IndexOf(sql));
    }

    [Fact]
    public async Task ProviderErrorNeverEscapesToDiagnosticsWithSimSecrets()
    {
        const string sentinel = "fixture-raw-sim-error";
        var sim = Source();
        var message = $"{sentinel}:{sim.PhoneNumber}:{sim.Secrets.Pin}:{sim.Secrets.Pin2}:{sim.Secrets.Puk}:{sim.Secrets.Puk2}";
        using var fixture = Create([.. Access(), .. Access(), Script.Rows(),
            Script.Error(SqlExceptionFactory.Create(-2, message))]);
        var attemptId = Guid.NewGuid();

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSimAsync(sim, EquipmentSimWriteMode.Add, null, null, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Unknown, failure.Phase);
        Assert.Equal(InventoryOperationState.Reserved, fixture.Journal.StateFor(attemptId));
        Assert.Single(fixture.Script.CommandLog, IsSimWrite);
        Assert.Empty(fixture.Errors.Entries);
        Assert.DoesNotContain(sentinel, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sim.PhoneNumber, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sim.Secrets.Puk, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sim.Secrets.Puk2, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokingRole42InsideReservationRejectsBeforeSimDml()
    {
        using var fixture = Create([.. Access(), .. Access(hasSimRole: false)]);
        var attemptId = Guid.NewGuid();

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSimAsync(Source(), EquipmentSimWriteMode.Add, null, null, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, failure.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsSimWrite);
        Assert.DoesNotContain(SQLQueries.SELECT_АктивныеДубликатыSIM, fixture.Script.CommandLog);
    }

    [Fact]
    public async Task EditRejectsWhenProtectedSnapshotHasChangedEvenWithMatchingAuditFields()
    {
        var original = Source();
        var changed = Source();
        changed.Secrets.Puk2 = "10022003";
        var requested = Source();
        requested.ContractId = 21;
        using var fixture = Create([.. Access(), .. Access(), Script.Rows(), SnapshotRow(changed)]);
        var attemptId = Guid.NewGuid();

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSimAsync(requested, EquipmentSimWriteMode.Edit, original.CaptureVersion(),
                null, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, failure.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.Contains(fixture.Script.CommandLog, sql => sql.Contains("AS Puk2", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsSimWrite);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteConfirmsOnlyWhenTheSourceSimIsAbsentAfterWriting(bool absent)
    {
        var original = Source();
        using var fixture = Create([.. Access(), .. Access(), SnapshotRow(original), Script.Scalar(1),
            absent ? Script.Rows() : SnapshotRow(original), .. Access()]);
        var attemptId = Guid.NewGuid();

        if (absent)
            await fixture.Service.DeleteSimAsync(EquipmentId, original.CaptureVersion(), EmployeeId, attemptId, default);
        else
        {
            var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
                fixture.Service.DeleteSimAsync(EquipmentId, original.CaptureVersion(), EmployeeId, attemptId, default));
            Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        }

        var expected = absent ? InventoryOperationState.Confirmed : InventoryOperationState.Rejected;
        Assert.Equal(new InventoryOperationReceipt(attemptId, expected, absent ? EquipmentId : null),
            await fixture.Service.ReadSimWriteReceiptAsync(EquipmentId, null, EquipmentFormService.DeleteSimCommand,
                EmployeeId, attemptId, default));
        Assert.Contains("DELETE vwSIMКарты", Assert.Single(fixture.Script.CommandLog, IsSimWrite));
    }

    [Fact]
    public async Task UnconfirmedActiveDuplicateIsRejectedBeforeWriting()
    {
        using var fixture = Create([.. Access(), .. Access(), Duplicate(DuplicateId)]);
        var id = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() => fixture.Service.SaveSimAsync(
            Source(), EquipmentSimWriteMode.Add, null, null, EmployeeId, id, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(id));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsSimWrite);
    }

    private static bool IsSimWrite(string sql) =>
        sql.Contains("INSERT vwSIMКарты", StringComparison.Ordinal) ||
        sql.Contains("UPDATE vwSIMКарты", StringComparison.Ordinal) ||
        sql.Contains("DELETE vwSIMКарты", StringComparison.Ordinal);

    private static ScriptEntry Duplicate(int id) => Script.Rows(Script.Row(("EquipmentId", id)));

    private static ScriptEntry[] Access(bool hasSimRole = true) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", hasSimRole ? (int)Роли.Администратор_тарифов_связи :
            (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
        ActualSimType(),
    ];

    private static ScriptEntry ActualSimType() => Script.Rows(Script.Row((nameof(EquipmentType.HasSimCharacteristics), (byte)1)));

    private static Fixture Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(script);
        var errors = new RecordingErrors();
        var db = new DbManager("Server=fixture", errors, () => journal);
        return new Fixture(new EquipmentFormService(db, new TestAuthenticationStateProvider()), script, journal, errors, db);
    }

    private sealed class RecordingErrors : ISqlErrorHandler
    {
        internal List<string> Entries { get; } = [];

        public void HandleSqlError(Microsoft.Data.SqlClient.SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters) =>
            Entries.Add(exception + commandText + string.Join(";", parameters.Select(parameter => parameter.Value)));
    }

    private sealed record Fixture(EquipmentFormService Service, ScriptedConnection Script,
        NetFormTests.NetJournalConnection Journal, RecordingErrors Errors, DbManager Db) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }
}
