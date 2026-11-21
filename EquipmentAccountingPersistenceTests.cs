using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки бухгалтерского учёта, прав и справочников.</summary>
public sealed class EquipmentAccountingPersistenceTests
{
    /// <summary>Материал очищает арендатора и номер; ОС требует номер и даты.</summary>
    [Fact]
    public void ValidateAndMaterialRules_WorkTogether()
    {
        var accounting = new EquipmentAccounting
        {
            OwnerId = 1,
            IsMaterial = true,
            TenantId = 2,
            InventoryNumber = "HIDDEN",
            DateFrom = new DateTime(2026, 11, 1),
        };
        accounting.ApplyMaterialRules();

        Assert.Null(accounting.TenantId);
        Assert.Equal(string.Empty, accounting.InventoryNumber);
        Assert.Null(accounting.Validate());
        accounting.IsMaterial = false;
        Assert.Equal("Укажите инвентарный номер основного средства.", accounting.Validate());
        accounting.InventoryNumber = "INV001";
        accounting.DateTo = accounting.DateFrom.Value.AddDays(-1);
        Assert.Equal("Дата поступления не может быть позже списания.", accounting.Validate());
    }

    /// <summary>Доступ требует роли 66/68 и функции бухгалтерского учёта.</summary>
    [Fact]
    public async Task CanUseAccounting_ChecksRoleAndFunction()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.Scalar(1)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        Assert.True(await service.CanUseAccountingAsync(1));

        Assert.Contains("КодРоли = @RoleId", connection.CommandLog[0]);
        Assert.Contains("fn_БухгалтерскийУчет", connection.CommandLog[1]);
    }

    /// <summary>Сохранение сначала проверяет права, затем выполняет INSERT.</summary>
    [Fact]
    public async Task SaveAccounting_ChecksAccessBeforeInsert()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Scalar(1), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.SaveAccountingAsync(new EquipmentAccounting
        {
            EquipmentId = 7,
            OwnerId = 1,
            DateFrom = new DateTime(2026, 11, 1),
            InventoryNumber = "INV001",
        });

        Assert.Equal(3, connection.CommandLog.Count);
        Assert.Contains("INSERT ОборудованиеБухУчета", connection.CommandLog[2]);
    }

    /// <summary>Удаление атомарно сверяет конкретный ID с последней записью и актуальными правами без изменения настроек сессии.</summary>
    [Fact]
    public async Task DeleteAccounting_UsesConfirmedIdAndAtomicLatestGuard()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.DeleteAccountingAsync(7, 41);

        var sql = Assert.Single(connection.CommandLog);
        Assert.DoesNotContain("SET TRANSACTION ISOLATION LEVEL", sql);
        Assert.DoesNotContain("SET XACT_ABORT", sql);
        Assert.Contains("UPDLOCK, HOLDLOCK", sql);
        Assert.Contains("ORDER BY От DESC, КодОборудованияБухУчета DESC", sql);
        Assert.Contains("@LatestId <> @КодОборудованияБухУчета", sql);
        Assert.Contains("@ActualOwnerId", sql);
        Assert.Contains("fn_ТекущиеРоли", sql);
    }

    /// <summary>Ошибка соединения при удалении не превращается в статус успешного удаления.</summary>
    [Fact]
    public async Task DeleteAccountingConnectivity_ThrowsInsteadOfSuccess()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry { Exception = SqlExceptionFactory.Create(53, "connection error") },
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await Assert.ThrowsAsync<SqlException>(() => service.DeleteAccountingAsync(7, 41));
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Отсутствие результата и неизвестный статус удаления отклоняются.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task DeleteAccountingMissingOrUnknownStatus_FailsClosed(int? status)
    {
        var entry = status is int value ? Script.Scalar(value) : new ScriptEntry();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([entry]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAccountingAsync(7, 41));
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Пустая история, более новая запись и отзыв роли отклоняются без success-path.</summary>
    [Theory]
    [InlineData(1, "уже удалена")]
    [InlineData(2, "изменилась")]
    [InlineData(3, "Недостаточно прав")]
    public async Task DeleteAccountingConflict_ReturnsWithoutDeleteSuccess(int status, string message)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(status)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => service.DeleteAccountingAsync(7, 41));
        Assert.Contains(message, exception.Message);
        if (status == 3)
            Assert.IsType<UnauthorizedAccessException>(exception);
        else
            Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>История восстанавливает владельца, арендатора, номер и даты.</summary>
    [Fact]
    public async Task GetAccounting_MapsHistory()
    {
        DapperColumnMapper.Initialize();
        var from = new DateTime(2026, 11, 1);
        var to = new DateTime(2026, 11, 17);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудованияБухУчета", 15), ("КодОборудования", 7),
                ("КодБухгалтерии", 1), ("Бухгалтерия", "Owner"), ("Материал", false),
                ("КодЛицаАрендатора", 2), ("Арендатор", "Tenant"), ("ИнвентарныйНомер", "INV001"),
                ("ДатаПоступления", from), ("ДатаСписания", to), ("Примечания", "note"),
                ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var history = await service.GetAccountingAsync(7);

        var item = Assert.Single(history);
        Assert.Equal("Owner", item.OwnerName);
        Assert.Equal("Tenant", item.TenantName);
        Assert.Equal("INV001", item.InventoryNumber);
        Assert.Equal(to, item.DateTo);
    }

    /// <summary>Поступление и списание имеют разные типы и общий фильтр владельца.</summary>
    [Fact]
    public async Task DocumentSources_UseConfirmedTypesAndOwnerFilter()
    {
        DapperColumnMapper.Initialize();
        var receiptConnection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var writeOffConnection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var receipt = new AccountingDocumentSelectDataSource(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => receiptConnection), writeOff: false);
        var writeOff = new AccountingDocumentSelectDataSource(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => writeOffConnection), writeOff: true);
        receipt.PersonId = writeOff.PersonId = 1;

        await receipt.SearchAsync(new ClaySelectSearchRequest { SearchText = "doc", Limit = 8 });
        await writeOff.SearchAsync(new ClaySelectSearchRequest { SearchText = "doc", Limit = 8 });

        Assert.Contains("IN (2079, 2145, 2070, 2071)", receiptConnection.CommandLog[0]);
        Assert.Contains("IN (2145, 2070)", writeOffConnection.CommandLog[0]);
        Assert.Contains("Parent.L <= DT.L", writeOffConnection.CommandLog[0]);
        Assert.All(new[] { receiptConnection.CommandLog[0], writeOffConnection.CommandLog[0] },
            sql => Assert.Contains("P.КодЛица = @PersonId", sql));
    }

    /// <summary>Справочник владельца ограничен функцией бухгалтерского учёта.</summary>
    [Fact]
    public async Task OwnerSource_UsesAccountingFunction()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new AccountingPersonSelectDataSource(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "owner", Limit = 8 });

        Assert.Contains("dbo.fn_БухгалтерскийУчет(КодЛица) = 1", connection.CommandLog[0]);
    }
}
