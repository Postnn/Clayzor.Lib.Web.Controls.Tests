using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки подключения рабочей сети.</summary>
public sealed class EquipmentNetworkPersistenceTests
{
    /// <summary>Отсутствие записи означает неподключённое состояние.</summary>
    [Fact]
    public async Task GetNetworkAttachmentCount_NoRowsReturnsZero()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        Assert.Equal(0, await service.GetNetworkAttachmentCountAsync(7));
        Assert.Contains("Открыто <> 0", connection.CommandLog[0]);
    }

    /// <summary>Оба направления проверяют исходное состояние и фиксируют результат вместе с SQL.</summary>
    [Theory]
    [InlineData(true, 0, 1, "INSERT vwЛокальнаяСетьПрисоединениеРабочейСети")]
    [InlineData(false, 2, 0, "UPDATE vwЛокальнаяСетьПрисоединениеРабочейСети")]
    public async Task AttachmentIsConfirmedByTheExactAttempt(bool attach, int expected, int result, string command)
    {
        var (service, connection, journal) = JournaledService([
            Actor, GlobalGrant, Actor, GlobalGrant, Computer, Script.Scalar(expected), Script.NonQuery(), Script.Scalar(result),
        ]);
        var attemptId = Guid.NewGuid();

        await service.SetNetworkAttachmentAsync(7, 4, attemptId, attach, expected, default);

        Assert.Single(connection.CommandLog, sql => sql.Contains(command, StringComparison.Ordinal));
        Assert.Equal(InventoryOperationState.Confirmed, journal.StateFor(attemptId));
        Assert.Equal(2, connection.CommandLog.Count(sql => sql.Contains("Открыто <> 0", StringComparison.Ordinal)));
    }

    /// <summary>Read-модель обратной ссылки заполняет ID, сетевое имя и порт из русских колонок.</summary>
    [Fact]
    public async Task NetworkPortLink_MapsLegacyColumns()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодЛокальнойСети", 11), ("СетевоеИмя", "clay-test-net"), ("Порт", (byte)12)))
        ]));
        var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);

        var rows = await db.QueryRequiredAsync<EquipmentNetworkPortLink>("SELECT КодЛокальнойСети, СетевоеИмя, Порт");

        var link = Assert.Single(rows);
        Assert.Equal(11, link.Id);
        Assert.Equal("clay-test-net", link.NetworkName);
        Assert.Equal((byte)12, link.Port);
        Assert.Equal("clay-test-net_12", link.DisplayName);
    }

    /// <summary>Неактуальные права и исходные данные не позволяют отправить запись.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRejectsRevokedRoleOrStaleCount(bool grantInside)
    {
        var (service, connection, journal) = JournaledService(grantInside
            ? [Actor, GlobalGrant, Actor, GlobalGrant, Computer, Script.Scalar(1)]
            : [Actor, GlobalGrant, Actor, Script.Rows(Script.Row(("RoleId", 103), ("PersonId", 1)))]);
        var attemptId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SetNetworkAttachmentAsync(7, 4, attemptId, true, 0, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, journal.StateFor(attemptId));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT vwЛокальнаяСетьПрисоединениеРабочейСети"));
    }

    /// <summary>После потери ответа сверка читает исходный GUID и повтор не выполняет DML.</summary>
    [Fact]
    public async Task AttachLostCommitReplyUsesDurableReceiptWithoutAnotherWrite()
    {
        var (service, connection, journal) = JournaledService([
            Actor, GlobalGrant, Actor, GlobalGrant, Computer, Script.Scalar(0), Script.NonQuery(), Script.Scalar(1),
            Actor, GlobalGrant, Actor, GlobalGrant,
        ]);
        var attemptId = Guid.NewGuid();
        journal.LoseNextConfirmedCommitReply = true;

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SetNetworkAttachmentAsync(7, 4, attemptId, true, 0, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        var receipt = await service.ReadNetworkWriteReceiptAsync(7, 4, attemptId, true, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, 7), receipt);
        await service.SetNetworkAttachmentAsync(7, 4, attemptId, true, 0, default);
        Assert.Single(connection.CommandLog, sql => sql.Contains("INSERT vwЛокальнаяСетьПрисоединениеРабочейСети"));
    }

    /// <summary>Другая незавершённая попытка блокирует отправку до резерва нового GUID.</summary>
    [Fact]
    public async Task OccupiedEquipmentScopeLeavesNewWorkNetCommandNotSent()
    {
        var (service, connection, journal) = JournaledService([Actor, GlobalGrant]);
        var older = InventoryOperationJournal.ForEmployee(new Employee { Id = 4 }, Guid.NewGuid(),
            "equipment/7", "equipment-save");
        var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => journal);
        await InventoryOperationJournal.ImportLegacyAsync(db, [older], default);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SetNetworkAttachmentAsync(7, 4, attemptId, true, 0, default));

        Assert.Equal(EquipmentWritePhase.NotSent, error.Phase);
        Assert.True(journal.HasLegacyEvidence(older.AttemptId));
        Assert.Null(journal.StateFor(attemptId));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT vwЛокальнаяСетьПрисоединениеРабочейСети"));
    }

    private static ScriptEntry Actor => Script.Rows(Script.Row(("КодСотрудника", 4)));
    private static ScriptEntry GlobalGrant => Script.Rows(Script.Row(("RoleId", 103), ("PersonId", 0)));
    private static ScriptEntry Computer => Script.Rows(Script.Row(("КодТипаОборудования", 1), ("ЕстьХарактеристикиКомпьютера", (byte)1)));

    private static (EquipmentFormService Service, ScriptedConnection Connection, NetFormTests.NetJournalConnection Journal)
        JournaledService(IEnumerable<ScriptEntry> commands)
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(commands), emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(connection);
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => journal),
            new TestAuthenticationStateProvider());
        return (service, connection, journal);
    }
}
