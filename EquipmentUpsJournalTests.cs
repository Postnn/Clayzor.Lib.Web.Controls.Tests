using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет серверное подтверждение подключения UPS и его связанных записей.</summary>
public sealed class EquipmentUpsJournalTests
{
    private const int ServerId = 7;
    private const int UpsId = 92;
    private const int EmployeeId = 4;
    private const int OwnerId = 5;
    private const int ServerTypeId = (int)ТипыОборудования.Компьютер;
    private const int UpsTypeId = 53;

    [Fact]
    public async Task LostAddLinkCommitUsesExactReceiptAndDoesNotRepeatDml()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, ServerId), .. Access(), .. Access()]);
        fixture.Journal.LoseNextConfirmedCommitReply = true;
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddUpsLinkAsync(UpsId, ServerId, EmployeeId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, ServerId),
            await fixture.Service.ReadUpsWriteReceiptAsync(UpsId, ServerId, EquipmentFormService.AddUpsLinkCommand,
                EmployeeId, attemptId, default));

        await fixture.Service.AddUpsLinkAsync(UpsId, ServerId, EmployeeId, attemptId, default);
        var sql = Assert.Single(fixture.Script.CommandLog, IsUpsWrite);
        var parameters = fixture.Script.ParameterLog[fixture.Script.CommandLog.IndexOf(sql)];
        Assert.Equal(UpsId, parameters["КодОборудованияUPS"]);
        Assert.Equal(ServerId, parameters["КодОборудованияСервера"]);
    }

    [Fact]
    public async Task ExactLinkReceiptCannotBeReadForAnotherUps()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, ServerId), .. Access(ups: UpsId + 1), .. Access()]);
        var attemptId = Guid.NewGuid();
        await fixture.Service.AddUpsLinkAsync(UpsId, ServerId, EmployeeId, attemptId, default);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            fixture.Service.ReadUpsWriteReceiptAsync(UpsId + 1, ServerId, EquipmentFormService.AddUpsLinkCommand,
                EmployeeId, attemptId, default));
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, ServerId),
            await fixture.Service.ReadUpsWriteReceiptAsync(UpsId, ServerId, EquipmentFormService.AddUpsLinkCommand,
                EmployeeId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsUpsWrite);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task FreshRoleOrActualServerTypeRevocationRejectsBeforeLinkDml(bool grant, bool simType)
    {
        using var fixture = Create([.. Access(), .. Access(grant: grant, simType: simType)]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddUpsLinkAsync(UpsId, ServerId, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsUpsWrite);
    }

    [Fact]
    public async Task DetailsReserveOnlyChangedServersInAdditionToSourceAndUps()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, ServerId), .. Access()]);
        var attemptId = Guid.NewGuid();
        var untouched = Link(19);
        UpsServerLink[] before = [Link(ServerId), Link(11), Link(12), untouched];
        UpsServerLink[] after = [Link(ServerId), Link(12, turnOff: 1), Link(15), untouched];

        await fixture.Service.SaveUpsDetailsAsync(UpsId, ServerId, before, after, [], [], EmployeeId, attemptId, default);

        Assert.Equal(new[] { "equipment/11", "equipment/12", "equipment/15", "equipment/92" },
            fixture.Journal.ScopesFor(attemptId).Order(StringComparer.Ordinal));
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, ServerId),
            await fixture.Service.ReadUpsWriteReceiptAsync(UpsId, ServerId, EquipmentFormService.SaveUpsDetailsCommand,
                EmployeeId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsUpsWrite);
    }

    [Fact]
    public async Task NamedLinkConflictDoesNotConfirmDeletion()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(2, 0), .. Access()]);
        var attemptId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.DeleteUpsLinkAsync(UpsId, ServerId, EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Rejected, null),
            await fixture.Service.ReadUpsWriteReceiptAsync(UpsId, ServerId, EquipmentFormService.DeleteUpsLinkCommand,
                EmployeeId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsUpsWrite);
    }

    [Fact]
    public async Task BatchProvesEachNewTestByItsOwnGeneratedKey()
    {
        using var fixture = Create([.. Access(), .. Access(), Proof(0, ServerId)]);
        var attemptId = Guid.NewGuid();
        var time = new DateTime(2026, 11, 27, 8, 30, 0, DateTimeKind.Utc);
        UpsTest[] tests =
        [
            new() { UpsEquipmentId = UpsId, TestedAt = time, BatteryTime = 10 },
            new() { UpsEquipmentId = UpsId, TestedAt = time, BatteryTime = 10 },
        ];
        await fixture.Service.SaveUpsDetailsAsync(UpsId, ServerId, [Link(ServerId)], [Link(ServerId)], [], tests,
            EmployeeId, attemptId, default);

        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        var sql = Assert.Single(fixture.Script.CommandLog, IsUpsWrite);
        Assert.Contains("DECLARE @testIns0Before TABLE", sql);
        Assert.Contains("DECLARE @testIns1Before TABLE", sql);
        Assert.Contains("КодТестирования = @testIns0NewId", sql);
        Assert.Contains("КодТестирования = @testIns1NewId", sql);
        Assert.DoesNotContain("DELETE vwПодключенияUPSтестирование", sql);
        Assert.DoesNotContain("COMMIT TRANSACTION", sql);
        Assert.DoesNotContain("ROLLBACK TRANSACTION", sql);
        Assert.DoesNotContain("SET NOCOUNT", sql);
    }

    private static bool IsUpsWrite(string sql) => sql.Contains("ConfirmedEquipmentId", StringComparison.Ordinal) &&
        sql.Contains("ПодключенияUPS", StringComparison.Ordinal);

    private static ScriptEntry Proof(int status, int id) =>
        Script.Rows(Script.Row(("Status", status), ("ConfirmedEquipmentId", id)));

    private static UpsServerLink Link(int server, byte turnOff = 0) => new()
    {
        UpsEquipmentId = UpsId, ServerEquipmentId = server, EquipmentTypeId = ServerTypeId,
        TurnOffConfigured = turnOff,
    };

    private static ScriptEntry[] Access(int ups = UpsId, bool grant = true, bool simType = false) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", grant ? (int)Роли.Специалист_по_инвентаризации_оборудования :
            (int)Роли.Администратор_тарифов_связи), ("PersonId", OwnerId))),
        Script.Rows(Script.Row(("КодОборудования", ServerId), ("КодЛицаВладельца", OwnerId),
            ("КодТипаОборудования", ServerTypeId))),
        Script.Rows(Script.Row(("КодТипаОборудования", ServerTypeId),
            ("ЕстьХарактеристикиSIM", simType ? (byte)1 : (byte)0))),
        Script.Rows(Script.Row(("КодОборудования", ups), ("КодТипаОборудования", UpsTypeId))),
    ];

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

    [Fact]
    public async Task LinkListCarriesExactSourceServerForTheEditor()
    {
        using var fixture = Create([Script.Rows(Script.Row(("КодОборудованияUPS", UpsId),
            ("КодОборудованияСервера", ServerId)))]);

        var link = Assert.Single(await fixture.Service.GetUpsLinksAsync(ServerId));

        Assert.Equal(ServerId, link.ServerEquipmentId);
        Assert.Contains("U.КодОборудованияСервера,", Assert.Single(fixture.Script.CommandLog));
    }
}
