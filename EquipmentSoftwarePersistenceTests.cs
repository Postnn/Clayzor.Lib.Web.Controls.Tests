using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки установленного ПО.</summary>
public sealed class EquipmentSoftwarePersistenceTests
{
    /// <summary>Обязательны дата, ПО, версия и дистрибутив; даты проверяются по порядку.</summary>
    [Fact]
    public void Validate_ChecksRequiredFieldsAndDates()
    {
        var item = new EquipmentSoftwareInstallation();

        Assert.Equal("Укажите дату установки.", item.Validate());
        item.InstalledAt = new DateTime(2026, 11, 18);
        Assert.Equal("Выберите программное обеспечение.", item.Validate());
        item.SoftwareId = 14;
        Assert.Equal("Укажите установленную версию.", item.Validate());
        item.Installed = "Version";
        Assert.Equal("Укажите дистрибутив.", item.Validate());
        item.Distribution = "Dist";
        item.UninstalledAt = item.InstalledAt.Value.AddDays(-1);
        Assert.Equal("Дата установки не может быть позже деинсталляции.", item.Validate());
    }

    /// <summary>INSERT применяет эксклюзивную дату деинсталляции.</summary>
    [Fact]
    public async Task InsertSoftware_UsesExclusiveEnd()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.SaveSoftwareInstallationAsync(ValidInstallation());

        Assert.Contains("INSERT vwОборудованиеПО", connection.CommandLog[0]);
        Assert.Contains("CASE WHEN @До IS NOT NULL THEN DATEADD(day, 1, @До) ELSE NULL END", connection.CommandLog[0]);
    }

    /// <summary>UPDATE использует идентификатор установки и ту же границу даты.</summary>
    [Fact]
    public async Task UpdateSoftware_UsesIdAndExclusiveEnd()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        var item = ValidInstallation();
        item.Id = 15;

        await service.SaveSoftwareInstallationAsync(item);

        Assert.Contains("UPDATE vwОборудованиеПО", connection.CommandLog[0]);
        Assert.Contains("WHERE КодОборудованияПО = @КодОборудованияПО", connection.CommandLog[0]);
        Assert.Contains("DATEADD(day, 1, @До)", connection.CommandLog[0]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[0]);
    }

    /// <summary>История восстанавливает название ПО, поля и включительную дату.</summary>
    [Fact]
    public async Task GetSoftware_MapsFieldsAndInclusiveEnd()
    {
        DapperColumnMapper.Initialize();
        var from = new DateTime(2026, 11, 1);
        var to = new DateTime(2026, 11, 17);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудованияПО", 15), ("КодОборудования", 7), ("КодПО", 14),
                ("НазваниеПО", "Software"), ("От", from), ("До", to), ("Деинсталировано", (byte)1),
                ("Установлено", "Version"), ("Дистрибутив", "Dist"), ("Примечания", "note"),
                ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var installations = await service.GetSoftwareInstallationsAsync(7);

        var item = Assert.Single(installations);
        Assert.Equal("Software", item.SoftwareName);
        Assert.Equal(to, item.UninstalledAt);
        Assert.Equal(1, item.IsUninstalled);
        Assert.Contains("DATEADD(day, -1, U.До)", connection.CommandLog[0]);
    }

    /// <summary>Справочник ПО ограничен типами больше 200 и восстанавливает модель.</summary>
    [Fact]
    public async Task SoftwareSource_UsesSoftwareTypesAndModel()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 14), ("ОтображаемоеНазвание", "Software"),
                ("МодельОборудования", "Version"))),
        ]));
        var source = new SoftwareEquipmentSelectDataSource(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var result = await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "soft", Limit = 8 });

        var item = Assert.Single(result.Items);
        Assert.Equal("Version", item.InstalledName);
        Assert.Contains("T.КодТипаОборудования > 200", connection.CommandLog[0]);
    }

    private static EquipmentSoftwareInstallation ValidInstallation() => new()
    {
        EquipmentId = 7,
        SoftwareId = 14,
        InstalledAt = new DateTime(2026, 11, 1),
        UninstalledAt = new DateTime(2026, 11, 17),
        Installed = "Version",
        Distribution = "Dist",
    };
}
