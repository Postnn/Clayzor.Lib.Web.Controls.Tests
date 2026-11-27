using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет авторизацию и подтверждение записи лицензии на границе серверного журнала.</summary>
public sealed class EquipmentLicenseJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private const int OwnerId = 2;
    private const int TenantId = 9;
    private const int EquipmentTypeId = 202;
    private const int LicenseTypeId = 12;
    private static readonly DateTime Version = new(2026, 11, 27, 10, 40, 0, DateTimeKind.Utc);

    [Fact]
    public async Task OwnTenantRole44WithoutInventoryRoleCanSaveAndReadExactReceipt()
    {
        using var fixture = Create([.. Access(), .. Access(), TypeRow(temporary: 1), Proof(EquipmentId), .. Access(), .. Access()]);
        var attemptId = Guid.NewGuid();
        var license = License(expectedExisting: true);
        var type = Type();
        type.Temporary = 1;
        license.DateFrom = Version.AddDays(-1);
        license.DateTo = Version;

        await fixture.Service.SaveLicenseAsync(license, type, EmployeeId, attemptId, default);

        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId),
            await fixture.Service.ReadLicenseWriteReceiptAsync(EquipmentId, true, EmployeeId, attemptId, default));
        Assert.False(await fixture.Service.ReadLicenseWritePendingAsync(EquipmentId, EmployeeId, default));
        var sql = Assert.Single(fixture.Script.CommandLog, IsLicenseWrite);
        var index = fixture.Script.CommandLog.IndexOf(sql);
        var typeRead = fixture.Script.CommandLog.IndexOf(SQLQueries.SELECT_ID_ТипЛицензирования);
        Assert.True(typeRead >= 0 && typeRead < index);
        var parameters = fixture.Script.ParameterLog[index];
        Assert.Equal(EquipmentId, parameters["КодОборудования"]);
        Assert.Equal(Version, parameters["ИсходноеИзменено"]);
        Assert.Equal(LicenseTypeId, parameters["КодТипаЛицензирования"]);
        Assert.Equal(Version.Date.AddDays(1), parameters["ДействуетДо"]);
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(TenantId, false)]
    public async Task FreshTenantOrEquipmentTypeChangeRejectsBeforeLicenseDml(int actualTenant, bool softwareType)
    {
        using var fixture = Create([.. Access(), .. Access(actualTenant, softwareType)]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveLicenseAsync(License(expectedExisting: true), Type(), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsLicenseWrite);
    }

    [Theory]
    [InlineData((byte)0, (byte)1)]
    [InlineData((byte)1, (byte)0)]
    public async Task FreshLicenseFlagsCannotBeReplacedByClientType(byte temporary, byte resale)
    {
        using var fixture = Create([.. Access(), .. Access(), TypeRow(temporary, resale)]);
        var attemptId = Guid.NewGuid();
        var submitted = License(expectedExisting: false);
        // These values satisfy either set of mandatory fields. The changed type itself must be rejected.
        submitted.DateFrom = Version.Date;
        submitted.DateTo = Version.Date.AddDays(1);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveLicenseAsync(submitted, Type(), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.Single(fixture.Script.CommandLog, sql => sql == SQLQueries.SELECT_ID_ТипЛицензирования);
        Assert.DoesNotContain(fixture.Script.CommandLog, IsLicenseWrite);
    }

    [Fact]
    public async Task NamedZeroForStaleVersionIsRejectedWithoutAConfirmedEquipmentId()
    {
        using var fixture = Create([.. Access(), .. Access(), TypeRow(), Proof(0), .. Access()]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveLicenseAsync(License(expectedExisting: true), Type(), EmployeeId, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Rejected, null),
            await fixture.Service.ReadLicenseWriteReceiptAsync(EquipmentId, true, EmployeeId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsLicenseWrite);
    }

    [Fact]
    public async Task LostCommitReturnsExactReceiptAndDoesNotRepeatWrite()
    {
        using var fixture = Create([.. Access(), .. Access(), TypeRow(), Proof(EquipmentId), .. Access(), .. Access()]);
        fixture.Journal.LoseNextConfirmedCommitReply = true;
        var attemptId = Guid.NewGuid();
        var license = License(expectedExisting: false);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveLicenseAsync(license, Type(), EmployeeId, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId),
            await fixture.Service.ReadLicenseWriteReceiptAsync(EquipmentId, false, EmployeeId, attemptId, default));

        await fixture.Service.SaveLicenseAsync(license, Type(), EmployeeId, attemptId, default);

        Assert.Single(fixture.Script.CommandLog, IsLicenseWrite);
    }

    [Fact]
    public async Task WrongResultColumnDoesNotReleasePendingSlotForRole44()
    {
        using var fixture = Create([.. Access(), .. Access(), TypeRow(),
            Script.Rows(Script.Row(("UnrelatedTriggerValue", EquipmentId))), .. Access()]);
        var id = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.SaveLicenseAsync(License(expectedExisting: false), Type(), EmployeeId, id, default));

        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Reserved, fixture.Journal.StateFor(id));
        Assert.True(await fixture.Service.ReadLicenseWritePendingAsync(EquipmentId, EmployeeId, default));
        Assert.Single(fixture.Script.CommandLog, IsLicenseWrite);
    }

    private static bool IsLicenseWrite(string sql) =>
        sql.Contains("ConfirmedEquipmentId", StringComparison.Ordinal) && sql.Contains("vwSoft", StringComparison.Ordinal);

    private static ScriptEntry[] Access(int actualTenant = TenantId, bool softwareType = true) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_по), ("PersonId", TenantId))),
        Script.Rows(Script.Row(("КодОборудования", EquipmentId), ("КодЛицаВладельца", OwnerId),
            ("КодЛицаАрендатора", actualTenant), ("КодТипаОборудования", EquipmentTypeId))),
        Script.Rows(Script.Row(("КодТипаОборудования", EquipmentTypeId), ("ЕстьХарактеристикиSoft", (byte)(softwareType ? 1 : 0)))),
    ];

    private static ScriptEntry TypeRow(byte temporary = 0, byte resale = 0) => Script.Rows(Script.Row(
        ("КодТипаЛицензирования", LicenseTypeId), ("Наименование", "Тип"), ("Временная", temporary), ("Перепродажа", resale)));

    private static LicenseType Type() => new() { Id = LicenseTypeId, Name = "Тип" };

    private static ScriptEntry Proof(int id) => Script.Rows(Script.Row(("ConfirmedEquipmentId", id)));

    private static EquipmentLicense License(bool expectedExisting) => new()
    {
        EquipmentId = EquipmentId, LicenseTypeId = LicenseTypeId, ExpectedExisting = expectedExisting, ChangedAt = Version,
        LicenseeNumber = "A0127", ServerCount = 0, ClientCount = 2, Distribution = "Дистрибутив",
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
