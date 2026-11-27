using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет долговечный журнал составных изменений периодов оборудования.</summary>
public sealed class EquipmentPeriodCompoundJournalTests
{
    private const int EquipmentId = 7;
    private const int ActorId = 4;
    private const int SimId = 18;
    private static readonly DateTime Start = new(2026, 11, 27, 8, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Version = Start.AddHours(2);

    [Fact]
    public async Task LostAddLocationCommitKeepsExactReceiptAndDoesNotRepeatPlanOrDml()
    {
        using var fixture = Create([.. Actor(), Plan(1), .. Actor(), Plan(1), Proof(), .. Actor(), .. Actor()]);
        fixture.Journal.LoseNextConfirmedCommitReply = true;
        var attemptId = Guid.NewGuid();
        var location = Location();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddLocationAsync(location, null, true, ActorId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));

        var receipt = await fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, Start,
            EquipmentFormService.AddLocationPeriodCommand, ActorId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), receipt);

        await fixture.Service.AddLocationAsync(location, null, true, ActorId, attemptId, default);

        Assert.Equal(2, fixture.Script.CommandLog.Count(sql => sql == SQLQueries.SELECT_ОборудованиеРасположениеПереносSim));
        var sql = Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
        Assert.Contains("INSERT ОборудованиеРасположение", sql);
        Assert.Equal("<sim><id>18</id></sim>", fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(sql)]["ExpectedSimXml"]);
        Assert.Contains(FormattableString.Invariant($"equipment/location-period/{EquipmentId}/{Start.Ticks}"),
            fixture.Journal.ScopesFor(attemptId));
        Assert.Contains("equipment/18", fixture.Journal.ScopesFor(attemptId));
    }

    [Fact]
    public async Task ChangedSimPlanRejectsAddLocationBeforeAnyPeriodDml()
    {
        using var fixture = Create([.. Actor(), Plan(1), .. Actor(), Plan(2)]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddLocationAsync(Location(), null, true, ActorId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.Equal(2, fixture.Script.CommandLog.Count(sql => sql == SQLQueries.SELECT_ОборудованиеРасположениеПереносSim));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsPeriodWrite);
    }

    [Fact]
    public async Task EnlistedSimConflictRejectsTheWholeMovement()
    {
        using var fixture = Create([.. Actor(), Plan(1), .. Actor(), Plan(1),
            Script.Rows(Script.Row(("Status", 11), ("ConfirmedEquipmentId", 0))), .. Actor()]);
        var id = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddLocationAsync(Location(), null, true, ActorId, id, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(new InventoryOperationReceipt(id, InventoryOperationState.Rejected, null),
            await fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, Start,
                EquipmentFormService.AddLocationPeriodCommand, ActorId, id, default));
        Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
    }

    [Fact]
    public async Task WithdrawWithoutRelocationCommitsTheExactResponsiblePeriodWithoutASimPlan()
    {
        using var fixture = Create([.. Actor(), .. Actor(), Proof(), .. Actor(), .. Actor()]);
        var attemptId = Guid.NewGuid();
        var current = new EquipmentResponsiblePeriod
        {
            EquipmentId = EquipmentId,
            FromUtc = Start,
            ToUtc = Version,
            EmployeeId = 8,
            ChangedAtUtc = Version,
        };

        await fixture.Service.WithdrawResponsibleAsync(current, null, null, null, ActorId, attemptId, default);

        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        Assert.Contains(FormattableString.Invariant($"equipment/responsible-period/{EquipmentId}/{Start.Ticks}"),
            fixture.Journal.ScopesFor(attemptId));
        Assert.DoesNotContain(SQLQueries.SELECT_ОборудованиеРасположениеПереносSim, fixture.Script.CommandLog);
        var sql = Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
        Assert.Contains("UPDATE ОборудованиеСотрудников", sql);
        Assert.DoesNotContain("INSERT ОборудованиеРасположение", sql);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, Start.AddSeconds(1),
                EquipmentFormService.WithdrawResponsiblePeriodCommand, ActorId, attemptId, default));
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId),
            await fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, Start,
                EquipmentFormService.WithdrawResponsiblePeriodCommand, ActorId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
    }

    [Fact]
    public async Task WithdrawalReservesRelocationAndNextDatesAndProvesLocationsAfterNextIssue()
    {
        using var fixture = Create([.. Actor(), Plan(1), .. Actor(), Plan(1), Proof()]);
        var id = Guid.NewGuid();
        var movedAt = Start.AddHours(1);
        var issuedAt = Start.AddHours(2);
        var current = new EquipmentResponsiblePeriod { EquipmentId = EquipmentId, FromUtc = Start,
            ToUtc = movedAt, ChangedAtUtc = Version, EmployeeId = 8 };
        var next = new EquipmentResponsiblePeriod { EquipmentId = EquipmentId, FromUtc = issuedAt,
            EmployeeId = 9, ReturnDate = issuedAt.Date };
        var location = Location();
        location.FromUtc = movedAt;

        await fixture.Service.WithdrawResponsibleAsync(current, next, location, issuedAt.Date, ActorId, id, default);

        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(id));
        var scopes = fixture.Journal.ScopesFor(id);
        Assert.Contains($"equipment/location-period/7/{movedAt.Ticks}", scopes);
        Assert.Contains($"equipment/responsible-period/7/{issuedAt.Ticks}", scopes);
        Assert.Contains("equipment/18", scopes);
        var sql = Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
        var issue = sql.IndexOf("SET @От = @ОтСледующего", StringComparison.Ordinal);
        Assert.True(issue >= 0 && issue < sql.IndexOf("P.От = @LocationFrom", StringComparison.Ordinal));
        var parameters = fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(sql)];
        Assert.Equal(movedAt, parameters["До"]);
        Assert.Equal(issuedAt, parameters["ОтСледующего"]);
    }

    private static EquipmentLocationPeriod Location() => new()
    {
        EquipmentId = EquipmentId, LocationId = 1005, FromUtc = Start, ChangedAtUtc = Version,
    };

    private static bool IsPeriodWrite(string sql) => sql.Contains("ConfirmedEquipmentId", StringComparison.Ordinal);

    private static ScriptEntry[] Actor() =>
    [
        Script.Rows(Script.Row(("КодСотрудника", ActorId))),
        Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
    ];

    private static ScriptEntry Plan(long rows) =>
        Script.Rows(Script.Row(("EquipmentId", SimId), ("SourceRowCount", rows)));

    private static ScriptEntry Proof() =>
        Script.Rows(Script.Row(("Status", 0), ("ConfirmedEquipmentId", EquipmentId)));

    private static Fixture Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(script);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => journal);
        return new Fixture(new EquipmentFormService(db, new TestAuthenticationStateProvider()), script, journal, db);
    }

    private sealed record Fixture(EquipmentFormService Service, ScriptedConnection Script,
        NetFormTests.NetJournalConnection Journal, DbManager Database) : IDisposable
    {
        public void Dispose() => Database.Dispose();
    }
}
