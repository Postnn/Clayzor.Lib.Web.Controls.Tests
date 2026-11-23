using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки контракта чтения и обновления основной записи оборудования.</summary>
public sealed class EquipmentFormPersistenceTests
{
    /// <summary>UPDATE содержит ключ и ровно 15 изменяемых полей legacy-контракта.</summary>
    [Fact]
    public void UpdateParameters_MatchLegacyContract()
    {
        var writtenOff = new DateTime(2026, 11, 17, 12, 30, 0, DateTimeKind.Utc);
        var equipment = new Equipment
        {
            Id = 7, SerialNumber = "SN", CameraIsConnected = 1, WriteOffAtUtc = writtenOff,
            DocumentWriteOffId = 8, NetworkName = "host", MacAddress = "001122334455",
            MacAddress2 = "66778899AABB", MacAddressIlo = "CCDDEEFF0011", Description = "note",
            OwnerId = 1, TenantId = 2, ShipperId = 3, PhonePoolId = 4, ModelId = 5,
            DocumentReceiptOuId = 6,
        };

        var parameters = equipment.CreateUpdateParameters();

        Assert.Equal(16, parameters.ParameterNames.Count());
        Assert.Equal(7, parameters.Get<int>("@КодОборудования"));
        Assert.Equal((byte)1, parameters.Get<byte>("@ПодключенаКамера"));
        Assert.Equal(writtenOff, parameters.Get<DateTime>("@Списано"));
        Assert.Equal(8, parameters.Get<int>("@КодДокументаСписания"));
        Assert.Equal(4, parameters.Get<int>("@КодПулаТелефонов"));
        Assert.Equal(5, parameters.Get<int>("@КодМоделиОборудования"));
        Assert.Equal(6, parameters.Get<int>("@КодДокументаПоступления"));
    }

    /// <summary>Обновление использует параметризованный UPDATE и не содержит INSERT.</summary>
    [Fact]
    public async Task Update_UsesConfirmedSql()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0))), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await service.UpdateAsync(new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1 });

        Assert.Equal(3, connection.CommandLog.Count);
        var sql = connection.CommandLog[2];
        Assert.Contains("UPDATE vwОборудование", sql);
        Assert.Contains("WHERE КодОборудования = @КодОборудования", sql);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Отсутствие затронутой записи не считается успешным сохранением.</summary>
    [Fact]
    public async Task UpdateWithoutAffectedRow_Throws()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0))), Script.NonQuery(0),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1 }));
    }

    /// <summary>Прямой backend-вызов без владельца отклоняется до UPDATE.</summary>
    [Fact]
    public async Task UpdateWithoutOwner_ThrowsBeforeSqlWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(new Equipment { Id = 7, ModelId = 5 }));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
    }

    /// <summary>Прямой backend-вызов с обязательным пустым SN отклоняется после чтения типа.</summary>
    [Fact]
    public async Task UpdateWithoutRequiredSn_ThrowsBeforeSqlWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)1), ("ЕстьMACадрес", (byte)0))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1 }));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
    }

    /// <summary>Прямой backend-вызов с неверным MAC отклоняется после чтения типа.</summary>
    [Fact]
    public async Task UpdateWithInvalidMac_ThrowsBeforeSqlWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)1))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1, MacAddress = "invalid" }));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
    }

    /// <summary>Полная выборка восстанавливает основные поля, модель и текущие связи.</summary>
    [Fact]
    public async Task Get_MapsAllEditableFieldsAndDisplayMetadata()
    {
        DapperColumnMapper.Initialize();
        var writtenOff = new DateTime(2026, 11, 17, 12, 30, 0);
        var changed = new DateTime(2026, 11, 18, 8, 15, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодОборудования", 7), ("КодМоделиОборудования", 5), ("КодТипаОборудования", 30),
                ("МодельОборудования", "Phone"), ("PN", "PN-1"), ("ТипОборудования", "IP-телефон"),
                ("SN", "SN"), ("ПодключенаКамера", (byte)1), ("Списано", writtenOff),
                ("КодДокументаСписания", 8), ("СетевоеИмя", "host"), ("MACадрес", "001122334455"),
                ("MACадрес2", "66778899AABB"), ("MACадресILO", "CCDDEEFF0011"),
                ("Примечания", "note"), ("КодЛицаВладельца", 1), ("КодЛицаАрендатора", 2),
                ("КодЛицаПоставщика", 3), ("КодПулаТелефонов", 4), ("КодДокументаПоступления", 6),
                ("Изменил", 4), ("Изменено", changed), ("ИзменилФИО", "Тестов А."),
                ("КодРасположения", 55), ("КодСотрудника", 9))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        var equipment = await service.GetAsync(7);

        Assert.NotNull(equipment);
        Assert.Equal(30, equipment.EquipmentTypeId);
        Assert.Equal("Phone", equipment.ModelName);
        Assert.Equal("PN-1", equipment.PartNumber);
        Assert.Equal("IP-телефон", equipment.EquipmentTypeName);
        Assert.Equal((byte)1, equipment.CameraIsConnected);
        Assert.Equal(writtenOff, equipment.WriteOffAtUtc);
        Assert.Equal(8, equipment.DocumentWriteOffId);
        Assert.Equal(4, equipment.PhonePoolId);
        Assert.Equal(6, equipment.DocumentReceiptOuId);
        Assert.Equal(4, equipment.ChangedById);
        Assert.Equal(changed, equipment.ChangedAtUtc);
        Assert.Equal("Тестов А.", equipment.ChangedByName);
        Assert.Equal(55, equipment.LocationId);
        Assert.Equal(9, equipment.EmployeeId);
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
