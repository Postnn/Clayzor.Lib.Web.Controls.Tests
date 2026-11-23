using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки чтения пояснения типа лицензии EQ8-12/H41.</summary>
public sealed class Eq8LicenseTypeReadTests
{
    /// <summary>Справочник возвращает legacy-колонку Описание вместе с правилами типа.</summary>
    [Fact]
    public async Task GetLicenseTypes_MapsDescription()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодТипаЛицензирования", 2),
                ("Наименование", "Temporary (Vendor)"),
                ("Описание", "Пояснение условий лицензии"),
                ("Временная", (byte)1),
                ("Перепродажа", (byte)0))),
        ]));
        var service = new EquipmentFormService(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());

        var types = await service.GetLicenseTypesAsync();

        var type = Assert.Single(types);
        Assert.Equal(2, type.Id);
        Assert.Equal("Temporary (Vendor)", type.Name);
        Assert.Equal("Пояснение условий лицензии", type.Description);
        Assert.Equal(1, type.Temporary);
        Assert.Equal(0, type.Resale);
        Assert.Contains("Описание", connection.CommandLog[0]);
    }
}
