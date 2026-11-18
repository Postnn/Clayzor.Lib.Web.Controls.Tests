using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки лицензий и правил типов лицензирования.</summary>
public sealed class EquipmentLicensePersistenceTests
{
    /// <summary>Бессрочная лицензия без перепродажи требует номер и количество клиентов.</summary>
    [Fact]
    public void ValidatePermanent_ChecksNumberAndCounts()
    {
        var type = new LicenseType { Id = 1 };
        var license = new EquipmentLicense { LicenseTypeId = 1, ServerCount = 0, ClientCount = 1 };

        Assert.Equal("Укажите номер лицензиата.", license.Validate(type));
        license.LicenseeNumber = "LIC";
        license.ClientCount = 0;
        Assert.Equal("Количество клиентов должно быть ненулевым.", license.Validate(type));
        license.ClientCount = 1;
        Assert.Null(license.Validate(type));
    }

    /// <summary>Временная лицензия требует обе даты в правильном порядке.</summary>
    [Fact]
    public void ValidateTemporary_ChecksDates()
    {
        var type = new LicenseType { Id = 2, Temporary = 1, Resale = 1 };
        var license = new EquipmentLicense { LicenseTypeId = 2, ServerCount = 1, ClientCount = 1 };

        Assert.Equal("Укажите срок действия временной лицензии.", license.Validate(type));
        license.DateFrom = new DateTime(2026, 12, 1);
        license.DateTo = new DateTime(2026, 11, 30);
        Assert.Equal("Начало действия не может быть позже окончания.", license.Validate(type));
    }

    /// <summary>Смена типа очищает номер при перепродаже и даты у бессрочной лицензии.</summary>
    [Fact]
    public void ApplyTypeRules_ClearsHiddenValues()
    {
        var license = new EquipmentLicense
        {
            LicenseeNumber = "HIDDEN",
            DateFrom = new DateTime(2026, 1, 1),
            DateTo = new DateTime(2026, 12, 1),
        };

        license.ApplyTypeRules(new LicenseType { Id = 3, Resale = 1, Temporary = 0 });

        Assert.Equal(3, license.LicenseTypeId);
        Assert.Equal(string.Empty, license.LicenseeNumber);
        Assert.Null(license.DateFrom);
        Assert.Null(license.DateTo);
    }

    /// <summary>UPSERT использует эксклюзивную дату окончания и не содержит DELETE.</summary>
    [Fact]
    public async Task SaveLicense_UsesUpsertAndExclusiveEnd()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 200), ("КодЛицаВладельца", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 200), ("ЕстьХарактеристикиSoft", (byte)1))),
            Script.Scalar(1), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        var type = new LicenseType { Id = 2, Temporary = 1, Resale = 1 };
        var license = new EquipmentLicense
        {
            EquipmentId = 7,
            LicenseTypeId = 2,
            ServerCount = 1,
            ClientCount = 2,
            DateFrom = new DateTime(2026, 1, 1),
            DateTo = new DateTime(2026, 12, 31),
        };

        await service.SaveLicenseAsync(license, type);

        Assert.Contains("IF EXISTS", connection.CommandLog[3]);
        Assert.Contains("INSERT vwSoft", connection.CommandLog[3]);
        Assert.Contains("ДействуетДо = @ДействуетДо", connection.CommandLog[3]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[3]);
    }

    /// <summary>Без роли 44 по владельцу лицензия не записывается.</summary>
    [Fact]
    public async Task SaveLicenseWithoutRole_DoesNotWrite()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 200), ("КодЛицаВладельца", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 200), ("ЕстьХарактеристикиSoft", (byte)1))),
            Script.Scalar(0),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveLicenseAsync(
            new EquipmentLicense { EquipmentId = 7 }, null));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT vwSoft"));
    }

    /// <summary>Чтение восстанавливает поля лицензии и включительную дату окончания.</summary>
    [Fact]
    public async Task GetLicenses_MapsFieldsAndUsesInclusiveEnd()
    {
        DapperColumnMapper.Initialize();
        var to = new DateTime(2026, 12, 31);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаЛицензирования", 2),
                ("ТипЛицензирования", "Temporary"), ("НомерЛицензиата", "LIC"),
                ("КоличествоСерверов", 1), ("КоличествоКлиентов", 2),
                ("ДействуетДо", to), ("Дистрибутив", "Dist"), ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var licenses = await service.GetLicensesAsync(7);

        var license = Assert.Single(licenses);
        Assert.Equal("Temporary", license.LicenseTypeName);
        Assert.Equal(to, license.DateTo);
        Assert.Equal(2, license.ClientCount);
        Assert.Contains("DATEADD(day, -1, S.ДействуетДо)", connection.CommandLog[0]);
    }
}
