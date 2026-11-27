using System.Data.SqlTypes;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет подтверждение точного периода расположения или ответственности.</summary>
public sealed class EquipmentPeriodSimpleJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private static readonly DateTime FromUtc = new DateTime(2026, 11, 27, 8, 30, 0, DateTimeKind.Utc).AddMilliseconds(2);
    private static readonly DateTime Version = new(2026, 11, 27, 11, 30, 0, DateTimeKind.Utc);
    private static DateTime SqlFrom => DateTime.SpecifyKind(new SqlDateTime(FromUtc).Value, DateTimeKind.Utc);

    [Fact]
    public async Task LostLocationCommitUsesSqlRoundedPeriodAndNeverRepeatsDml()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, EquipmentId), .. Access(), .. Access()]);
        fixture.Journal.LoseNextConfirmedCommitReply = true;
        var attemptId = Guid.NewGuid();
        var location = Location();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.UpdateLocationAsync(location, EmployeeId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        Assert.Equal(PeriodScope("location-period"), Assert.Single(fixture.Journal.ScopesFor(attemptId)));
        var receipt = await fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, SqlFrom,
            EquipmentFormService.UpdateLocationPeriodCommand, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), receipt);

        await fixture.Service.UpdateLocationAsync(location, EmployeeId, attemptId, default);
        var sql = Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
        Assert.Contains("ОборудованиеРасположение", sql);
        var parameters = fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(sql)];
        Assert.Equal(Version, parameters["ИсходноеИзменено"]);
        Assert.Equal(SqlFrom, parameters["От"]);
    }

    [Fact]
    public async Task SameEquipmentAndGuidDoNotProveAnotherResponsiblePeriod()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, EquipmentId), .. Access(), .. Access()]);
        var attemptId = Guid.NewGuid();
        await fixture.Service.IssueResponsibleAsync(Responsible(), EmployeeId, attemptId, default);

        Assert.Equal(PeriodScope("responsible-period"), Assert.Single(fixture.Journal.ScopesFor(attemptId)));
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, SqlFrom.AddSeconds(1),
                EquipmentFormService.IssueResponsiblePeriodCommand, EmployeeId, attemptId, default));
        var own = await fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, SqlFrom,
            EquipmentFormService.IssueResponsiblePeriodCommand, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), own);
        var sql = Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
        Assert.Contains("AND От = @От", sql);
        Assert.DoesNotContain("До IS NULL", sql);
        Assert.DoesNotContain("От >= @От", sql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedRefusalForStalePeriodOrDuplicateKeyDoesNotConfirm(bool issue)
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(issue ? 2 : 1, 0), .. Access()]);
        var attemptId = Guid.NewGuid();
        var responsible = Responsible();
        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() => issue
            ? fixture.Service.IssueResponsibleAsync(responsible, EmployeeId, attemptId, default)
            : fixture.Service.UpdateResponsibleAsync(responsible, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        var kind = issue ? EquipmentFormService.IssueResponsiblePeriodCommand : EquipmentFormService.UpdateResponsiblePeriodCommand;
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Rejected, null),
            await fixture.Service.ReadPeriodWriteReceiptAsync(EquipmentId, SqlFrom, kind, EmployeeId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
    }

    [Fact]
    public async Task EditingTheLastClosedPeriodKeepsTheOriginalEndInTheCas()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, EquipmentId)]);
        var attemptId = Guid.NewGuid();
        var responsible = Responsible();
        responsible.ToUtc = FromUtc.AddDays(1);

        await fixture.Service.UpdateResponsibleAsync(responsible, EmployeeId, attemptId, default);

        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        var sql = Assert.Single(fixture.Script.CommandLog, IsPeriodWrite);
        var parameters = fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(sql)];
        Assert.Contains("OR До = @ИсходноеДо", sql);
        Assert.Equal(responsible.ToUtc, parameters["ИсходноеДо"]);
        Assert.Equal(responsible.EmployeeId, parameters["КодСотрудника"]);
    }

    [Fact]
    public async Task RevocationInsideTransactionPreventsResponsibleUpdate()
    {
        using var fixture = Create([.. Access(), .. Access(role43: false)]);
        var attemptId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.UpdateResponsibleAsync(Responsible(), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsPeriodWrite);
    }

    private static string PeriodScope(string kind) =>
        FormattableString.Invariant($"equipment/{kind}/{EquipmentId}/{SqlFrom.Ticks}");

    private static bool IsPeriodWrite(string sql) => sql.Contains("ConfirmedEquipmentId", StringComparison.Ordinal) &&
        (sql.Contains("ОборудованиеРасположение", StringComparison.Ordinal) ||
         sql.Contains("ОборудованиеСотрудников", StringComparison.Ordinal));

    private static ScriptEntry[] Access(bool role43 = true) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", role43 ? (int)Роли.Специалист_по_инвентаризации_оборудования :
                (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))),
    ];

    private static ScriptEntry Proof(int status, int resultId) =>
        Script.Rows(Script.Row(("Status", status), ("ConfirmedEquipmentId", resultId)));

    private static EquipmentLocationPeriod Location() => new()
    {
        EquipmentId = EquipmentId, FromUtc = FromUtc, LocationId = 3,
        ChangedAtUtc = Version, Notes = "Период расположения",
    };

    private static EquipmentResponsiblePeriod Responsible() => new()
    {
        EquipmentId = EquipmentId, FromUtc = FromUtc, EmployeeId = 8,
        ChangedAtUtc = Version, Notes = "Период ответственности",
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
