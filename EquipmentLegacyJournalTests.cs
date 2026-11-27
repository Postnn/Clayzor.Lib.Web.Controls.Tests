using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет перенос прежнего результата Equipment с актуальными правами и точным серверным ключом.</summary>
public sealed class EquipmentLegacyJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private const string Scope = "equipment/7";

    [Fact]
    public async Task LegacySoftwareWithoutReceiptBlocksTheActualEquipmentScope()
    {
        using var fixture = Create([.. Actor(Роли.Специалист_по_инвентаризации_оборудования), Equipment(), ComputerType(),
            .. Actor(Роли.Специалист_по_инвентаризации_оборудования), Equipment()]);
        var id = Guid.NewGuid();

        await fixture.Service.ImportEquipmentLegacyEvidenceAsync(EquipmentId, EmployeeId,
            [Evidence(id, "Сохранение установленного ПО")], default);

        Assert.True(fixture.Journal.HasLegacyEvidence(id));
        Assert.True(await fixture.Service.ReadMainWritePendingAsync(EquipmentId, EmployeeId, default));
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("INSERT vwОборудованиеПО", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExactNetworkReceiptNeedsCurrentSupportRoleAndDoesNotCreateLegacyMarker()
    {
        using var fixture = Create([.. Actor(Роли.Исполнитель_заявок_службы_поддержки_пользователей), Equipment(), ComputerType()]);
        var id = Guid.NewGuid();
        await SeedConfirmedAsync(fixture, id, Scope, EquipmentFormService.AttachNetworkCommand, []);

        await fixture.Service.ImportEquipmentLegacyEvidenceAsync(EquipmentId, EmployeeId,
            [Evidence(id, EquipmentFormService.AttachNetworkCommand)], default);

        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(id));
        Assert.False(fixture.Journal.HasLegacyEvidence(id));
    }

    [Fact]
    public async Task InventoryRoleAloneCannotImportNetworkAction()
    {
        using var fixture = Create([.. Actor(Роли.Специалист_по_инвентаризации_оборудования), Equipment(), ComputerType()]);
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ImportEquipmentLegacyEvidenceAsync(
            EquipmentId, EmployeeId, [Evidence(id, EquipmentFormService.DetachNetworkCommand)], default));

        Assert.False(fixture.Journal.HasLegacyEvidence(id));
    }

    [Fact]
    public async Task AnotherEquipmentReceiptCannotBeTransferredToTheCurrentCard()
    {
        using var fixture = Create([.. Actor(Роли.Специалист_по_инвентаризации_оборудования), Equipment()]);
        var id = Guid.NewGuid();
        await SeedConfirmedAsync(fixture, id, "equipment/8", "equipment-save", []);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() => fixture.Service.ImportEquipmentLegacyEvidenceAsync(
            EquipmentId, EmployeeId, [Evidence(id, "equipment-save")], default));

        Assert.False(fixture.Journal.HasLegacyEvidence(id));
    }

    [Fact]
    public async Task SqlPhoneSaveProofWithoutAtsWorkflowRetainsAnUnresolvedMarker()
    {
        using var fixture = Create([.. Actor(Роли.Специалист_по_инвентаризации_оборудования), Equipment(), Script.Rows()]);
        var id = Guid.NewGuid();
        await SeedConfirmedAsync(fixture, id, Scope, "equipment-phone-save", ["tel/18"]);

        await fixture.Service.ImportEquipmentLegacyEvidenceAsync(EquipmentId, EmployeeId,
            [Evidence(id, "equipment-phone-save")], default);

        Assert.True(fixture.Journal.HasLegacyEvidence(id));
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(id));
        Assert.Contains("tel/18", fixture.Journal.ScopesFor(id));
    }

    [Fact]
    public async Task UnprovedDuplicateDoesNotInventHistoricalSideScope()
    {
        using var fixture = Create([.. Actor(Роли.Администратор_тарифов_связи), Equipment(),
            Script.Rows(Script.Row(("ЕстьХарактеристикиSIM", (byte)1)))]);
        var id = Guid.NewGuid();

        await fixture.Service.ImportEquipmentLegacyEvidenceAsync(EquipmentId, EmployeeId,
            [Evidence(id, EquipmentFormService.InsertSimCommand, """{"DuplicateEquipmentId":8}""")], default);

        Assert.True(fixture.Journal.HasLegacyEvidence(id));
        Assert.Empty(fixture.Journal.ScopesFor(id));
    }

    [Fact]
    public async Task UnexpectedAccountDataFailsBeforeSql()
    {
        using var fixture = Create([]);
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ImportEquipmentLegacyEvidenceAsync(
            EquipmentId, EmployeeId, [Evidence(id, EquipmentFormService.UpdateAccountCommand,
                """{"Binding":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","Login":"fixture"}""")], default));

        Assert.Empty(fixture.Connection.CommandLog);
        Assert.False(fixture.Journal.HasLegacyEvidence(id));
    }

    private static Task<InventoryOperationReceipt> SeedConfirmedAsync(Fixture fixture, Guid id, string scope, string kind,
        IReadOnlyCollection<string> related) => InventoryOperationJournal.RunSqlAsync<InvalidOperationException>(fixture.Db,
            InventoryOperationJournal.ForEmployee(new Employee { Id = EmployeeId }, id, scope, kind, related),
            (_, _, _) => Task.FromResult<int?>(EquipmentId), default);

    private static ClayLegacyDraftOperationEvidence Evidence(Guid id, string kind, string data = "{}") =>
        new(id.ToString("N"), kind, data);

    private static ScriptEntry[] Actor(Роли role) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", (int)role), ("PersonId", 0))),
    ];

    private static ScriptEntry Equipment() => Script.Rows(Script.Row(("КодОборудования", EquipmentId),
        ("КодЛицаВладельца", 9), ("КодТипаОборудования", 1)));

    private static ScriptEntry ComputerType() => Script.Rows(Script.Row(("ЕстьХарактеристикиКомпьютера", (byte)1)));

    private static Fixture Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(script);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => journal);
        return new Fixture(new EquipmentFormService(db, new TestAuthenticationStateProvider()), script, journal, db);
    }

    private sealed record Fixture(EquipmentFormService Service, ScriptedConnection Connection,
        NetFormTests.NetJournalConnection Journal, DbManager Db) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }
}
