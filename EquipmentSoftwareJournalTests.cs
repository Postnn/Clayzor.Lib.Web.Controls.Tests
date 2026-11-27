using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет серверную привязку результата установки ПО к попытке и оборудованию.</summary>
public sealed class EquipmentSoftwareJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private const int SoftwareId = 14;
    private const int InstallationId = 51;
    private static readonly DateTime ChangedAt = new(2026, 11, 27, 9, 42, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveConfirmsExactInstallationKeyUnderTheSameAttempt(bool update)
    {
        using var fixture = Create(Proof(InstallationId), receiptReads: 1);
        var installation = Installation(update ? InstallationId : 0);
        var attemptId = Guid.NewGuid();

        await fixture.Service.SaveSoftwareInstallationAsync(installation, EmployeeId, attemptId, default);

        var receipt = await fixture.Service.ReadSoftwareWriteReceiptAsync(EquipmentId, installation.Id,
            EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, InstallationId), receipt);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        if (update)
            Assert.Equal("equipment/software/51", Assert.Single(fixture.Journal.ScopesFor(attemptId)));
        else
            Assert.Empty(fixture.Journal.ScopesFor(attemptId));

        var statement = Assert.Single(fixture.Script.CommandLog, IsSoftwareWrite);
        Assert.Equal(update ? SQLQueries.UPDATE_ID_ОборудованиеПОEnlisted : SQLQueries.INSERT_ОборудованиеПОEnlisted, statement);
        var parameters = fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(statement)];
        Assert.Equal(EquipmentId, parameters["КодОборудования"]);
        if (update)
        {
            Assert.Equal(installation.Id, parameters["КодОборудованияПО"]);
            Assert.Equal(EquipmentId, parameters["ИсходныйКодОборудования"]);
            Assert.Equal(ChangedAt, parameters["ИсходноеИзменено"]);
        }
        else
            Assert.DoesNotContain("КодОборудованияПО", parameters.Keys);
        Assert.Equal(SoftwareId, parameters["КодПО"]);
        Assert.Equal(installation.UninstalledAt!.Value.Date, parameters["До"]);
    }

    [Fact]
    public async Task StaleUpdateWithNamedZeroIsRejectedWithoutAConfirmedKey()
    {
        using var fixture = Create(Proof(0), receiptReads: 1);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSoftwareInstallationAsync(Installation(InstallationId), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        var receipt = await fixture.Service.ReadSoftwareWriteReceiptAsync(EquipmentId, InstallationId,
            EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Rejected, null), receipt);
        Assert.Single(fixture.Script.CommandLog, IsSoftwareWrite);
    }

    [Fact]
    public async Task RevokedRoleInsideReservationRejectsBeforeTargetReadAndDml()
    {
        using var fixture = Create(proof: null, grantInside: false);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSoftwareInstallationAsync(Installation(0), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, fixture.Script.CommandLog);
        Assert.DoesNotContain(fixture.Script.CommandLog, IsSoftwareWrite);
    }

    [Fact]
    public async Task LostInsertCommitReturnsExactReceiptAndDoesNotInsertAgain()
    {
        using var fixture = Create(Proof(InstallationId), receiptReads: 1, replay: true);
        var attemptId = Guid.NewGuid();
        var installation = Installation(0);
        fixture.Journal.LoseNextConfirmedCommitReply = true;

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSoftwareInstallationAsync(installation, EmployeeId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));

        var receipt = await fixture.Service.ReadSoftwareWriteReceiptAsync(EquipmentId, 0, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, InstallationId), receipt);
        await fixture.Service.SaveSoftwareInstallationAsync(installation, EmployeeId, attemptId, default);

        Assert.Equal(SQLQueries.INSERT_ОборудованиеПОEnlisted, Assert.Single(fixture.Script.CommandLog, IsSoftwareWrite));
    }

    [Fact]
    public async Task PositiveValueInUnexpectedColumnIsNotProofAndCannotBeReplayed()
    {
        using var fixture = Create(Script.Rows(Script.Row(("UnexpectedTriggerValue", InstallationId))),
            receiptReads: 1, replay: true);
        var attemptId = Guid.NewGuid();
        var installation = Installation(0);

        var first = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSoftwareInstallationAsync(installation, EmployeeId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, first.Phase);
        Assert.Equal(InventoryOperationState.Reserved, fixture.Journal.StateFor(attemptId));
        var receipt = await fixture.Service.ReadSoftwareWriteReceiptAsync(EquipmentId, 0, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Reserved, null), receipt);

        await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveSoftwareInstallationAsync(installation, EmployeeId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsSoftwareWrite);
    }

    [Fact]
    public async Task ConfirmedUpdateReceiptCannotBeReadForAnotherInstallation()
    {
        using var fixture = Create(Proof(InstallationId), receiptReads: 2);
        var attemptId = Guid.NewGuid();
        await fixture.Service.SaveSoftwareInstallationAsync(Installation(InstallationId), EmployeeId, attemptId, default);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            fixture.Service.ReadSoftwareWriteReceiptAsync(EquipmentId, InstallationId + 1, EmployeeId, attemptId, default));

        var own = await fixture.Service.ReadSoftwareWriteReceiptAsync(EquipmentId, InstallationId, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, InstallationId), own);
        Assert.Single(fixture.Script.CommandLog, IsSoftwareWrite);
    }

    private static ScriptEntry Proof(int resultId) => Script.Rows(Script.Row(("ConfirmedInstallationId", resultId)));

    private static bool IsSoftwareWrite(string sql) =>
        sql == SQLQueries.INSERT_ОборудованиеПОEnlisted || sql == SQLQueries.UPDATE_ID_ОборудованиеПОEnlisted;

    private static EquipmentSoftwareInstallation Installation(int id) => new()
    {
        Id = id, EquipmentId = EquipmentId, SoftwareId = SoftwareId,
        InstalledAt = ChangedAt.AddDays(-1), UninstalledAt = ChangedAt.AddDays(1),
        Installed = "Версия", Distribution = "Дистрибутив", ChangedAt = ChangedAt,
    };

    private static Fixture Create(ScriptEntry? proof, bool grantInside = true, int receiptReads = 0, bool replay = false)
    {
        DapperColumnMapper.Initialize();
        var actor = Script.Rows(Script.Row(("КодСотрудника", EmployeeId)));
        var roles = Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)));
        var entries = new Queue<ScriptEntry>([actor, roles, actor, grantInside ? roles : Script.Rows()]);
        if (grantInside)
        {
            entries.Enqueue(Script.Rows(Script.Row(("ЕстьХарактеристикиКомпьютера", (byte)1))));
            entries.Enqueue(Script.Scalar(SoftwareId));
            entries.Enqueue(proof!);
        }
        for (var read = 0; read < receiptReads; read++)
        {
            entries.Enqueue(actor);
            entries.Enqueue(roles);
            entries.Enqueue(Script.Rows(Script.Row(("КодОборудования", EquipmentId))));
        }
        if (replay)
        {
            entries.Enqueue(actor);
            entries.Enqueue(roles);
        }
        var script = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(script);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => journal);
        return new Fixture(new EquipmentFormService(db, new TestAuthenticationStateProvider()), script, journal, db);
    }

    private sealed record Fixture(EquipmentFormService Service, ScriptedConnection Script,
        NetFormTests.NetJournalConnection Journal, DbManager Db) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }
}
