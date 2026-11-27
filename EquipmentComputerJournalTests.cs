using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет привязку серверного результата к дневному снимку компьютера.</summary>
public sealed class EquipmentComputerJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private const int ModelId = 5;
    private static readonly DateTime Day = new(2026, 11, 27);
    private static readonly DateTime Version = new(2026, 11, 27, 12, 14, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LostCommitReturnsOnlyExactDayReceiptAndDoesNotRepeatWrite()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, EquipmentId), .. Access(), .. Access()]);
        fixture.Journal.LoseNextConfirmedCommitReply = true;
        var attemptId = Guid.NewGuid();
        var snapshot = Snapshot(expectedExisting: false);

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveComputerPhasedAsync(snapshot, Day, ModelId, EmployeeId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, failure.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));

        var receipt = await fixture.Service.ReadComputerWriteReceiptAsync(EquipmentId, Day, false, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), receipt);
        Assert.Equal("equipment/computer/7/20261127", Assert.Single(fixture.Journal.ScopesFor(attemptId)));

        await fixture.Service.SaveComputerPhasedAsync(snapshot, Day, ModelId, EmployeeId, attemptId, default);
        var dml = Assert.Single(fixture.Script.CommandLog, IsComputerWrite);
        var parameters = fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(dml)];
        Assert.Equal(EquipmentId, parameters["КодОборудования"]);
        Assert.Equal(Day, parameters["С"]);
        Assert.Equal(0, parameters["ОжидаетсяСуществующий"]);
    }

    [Fact]
    public async Task SameEquipmentAndGuidDoNotProveAnotherCalendarDay()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, EquipmentId), .. Access(), .. Access()]);
        var attemptId = Guid.NewGuid();
        await fixture.Service.SaveComputerPhasedAsync(Snapshot(expectedExisting: true), Day, ModelId, EmployeeId, attemptId, default);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            fixture.Service.ReadComputerWriteReceiptAsync(EquipmentId, Day.AddDays(-1), true, EmployeeId, attemptId, default));

        var receipt = await fixture.Service.ReadComputerWriteReceiptAsync(EquipmentId, Day, true, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), receipt);
        Assert.Single(fixture.Script.CommandLog, IsComputerWrite);
    }

    [Theory]
    [InlineData(false, ModelId, 2)]
    [InlineData(true, ModelId + 1, 2)]
    [InlineData(true, ModelId, 1)]
    public async Task FreshRoleModelOrBanksChangePreventsDml(bool writable, int model, int banks)
    {
        using var fixture = Create([.. Access(), .. Access(writable, model, banks)]);
        var attemptId = Guid.NewGuid();

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveComputerPhasedAsync(Snapshot(expectedExisting: true), Day, ModelId, EmployeeId,
                attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, failure.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsComputerWrite);
    }

    [Theory]
    [InlineData(true, EquipmentWritePhase.NotSent, InventoryOperationState.Rejected)]
    [InlineData(false, EquipmentWritePhase.Unknown, InventoryOperationState.Reserved)]
    public async Task TransportFaultBeforeDmlIsDistinguishedFromLossAfterDispatch(bool failsInFreshRead,
        EquipmentWritePhase expectedPhase, InventoryOperationState expectedState)
    {
        var timeout = Script.Error(SqlExceptionFactory.Create(-2, "fixture timeout"));
        ScriptEntry[] fault = failsInFreshRead ? [timeout] : [.. Access(), timeout];
        using var fixture = Create([.. Access(), .. fault, .. Access(), .. Access(), Proof(0, EquipmentId)]);
        var attemptId = Guid.NewGuid();
        var snapshot = Snapshot(expectedExisting: false);

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveComputerPhasedAsync(snapshot, Day, ModelId, EmployeeId, attemptId, default));

        Assert.Equal(expectedPhase, failure.Phase);
        Assert.Equal(expectedState, fixture.Journal.StateFor(attemptId));
        if (!failsInFreshRead)
        {
            Assert.Single(fixture.Script.CommandLog, IsComputerWrite);
            return;
        }
        Assert.DoesNotContain(fixture.Script.CommandLog, IsComputerWrite);
        await fixture.Service.SaveComputerPhasedAsync(snapshot, Day, ModelId, EmployeeId, Guid.NewGuid(), default);
        Assert.Single(fixture.Script.CommandLog, IsComputerWrite);
    }

    [Fact]
    public async Task MissingBusinessDayCannotConfirmComputerSnapshot()
    {
        using var fixture = Create(Access());
        var attemptId = Guid.NewGuid();

        await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveComputerPhasedAsync(Snapshot(expectedExisting: false), null, ModelId, EmployeeId,
                attemptId, default));

        Assert.NotEqual(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsComputerWrite);
    }

    [Fact]
    public async Task NamedConflictHasNoConfirmedEquipmentId()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(1, 0), .. Access()]);
        var attemptId = Guid.NewGuid();

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveComputerPhasedAsync(Snapshot(expectedExisting: true), Day, ModelId, EmployeeId,
                attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, failure.Phase);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Rejected, null),
            await fixture.Service.ReadComputerWriteReceiptAsync(EquipmentId, Day, true, EmployeeId, attemptId, default));
        var sql = Assert.Single(fixture.Script.CommandLog, IsComputerWrite);
        Assert.Equal(Version, fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(sql)]["ИсходноеИзменено"]);
    }

    [Fact]
    public async Task TooLargeRamVolumeIsRejectedBeforeDmlWithoutHoldingTheScope()
    {
        using var fixture = Create([.. Access(), .. Access()]);
        var attemptId = Guid.NewGuid();
        var snapshot = Snapshot(expectedExisting: false);
        snapshot.RamComponents = [(decimal)int.MaxValue + 1];

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveComputerPhasedAsync(snapshot, Day, ModelId, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsComputerWrite);
    }

    private static bool IsComputerWrite(string sql) => sql.Contains("ConfirmedEquipmentId", StringComparison.Ordinal) &&
        sql.Contains("Компьютеры", StringComparison.Ordinal);

    private static ScriptEntry Proof(int status, int resultId) =>
        Script.Rows(Script.Row(("Status", status), ("ConfirmedEquipmentId", resultId)));

    private static ScriptEntry[] Access(bool writable = true, int model = ModelId, int banks = 2) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        writable
            ? Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)))
            : Script.Rows(),
        Script.Rows(Script.Row(("ModelId", model), ("EquipmentTypeId", (int)ТипыОборудования.Компьютер),
            ("HasComputerCharacteristics", (byte)1), ("MemoryBanks", banks))),
    ];

    private static EquipmentComputerSnapshot Snapshot(bool expectedExisting) => new()
    {
        EquipmentId = EquipmentId, Date = Day, ExpectedExisting = expectedExisting, ChangedAt = Version,
        RamComponents = [1024m, 2048m],
    };

    private static Fixture Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
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
