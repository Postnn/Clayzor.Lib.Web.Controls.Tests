using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки дополнительных услуг SIM.</summary>
public sealed class EquipmentSimServicePersistenceTests
{
    /// <summary>Проверяются обязательные поля, порядок дат и запрет будущих фактических дат.</summary>
    [Fact]
    public void Validate_ChecksServiceAndDates()
    {
        var today = new DateTime(2026, 11, 18);
        var service = new EquipmentSimService { DateFrom = today };

        Assert.Equal("Выберите тип услуги.", service.Validate(today));
        service.ServiceTypeId = "_TEST";
        Assert.Equal("Укажите название услуги.", service.Validate(today));
        service.ServiceName = "Test";
        service.DateTo = today.AddDays(-1);
        Assert.Equal("Дата подключения не может быть позже даты прекращения.", service.Validate(today));
        service.DateFrom = today.AddDays(1);
        service.DateTo = null;
        Assert.Equal("Фактические даты не могут быть в будущем.", service.Validate(today));
    }

    /// <summary>SQL использует эксклюзивное окончание и симметричные границы для открытых интервалов.</summary>
    [Fact]
    public async Task SaveService_UsesExclusiveEndAndSymmetricOverlap()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.SaveSimServiceAsync(new EquipmentSimService
        {
            EquipmentId = 7,
            PhoneNumber = "79000000001",
            ServiceTypeId = "_TEST",
            ServiceName = "Test",
            DateFrom = DateTime.Today,
        });

        Assert.Contains("INSERT vwSIMКартыДопУслугиСотОператора", connection.CommandLog[1]);
        Assert.Contains("@От < ISNULL(До, '20500101')", connection.CommandLog[1]);
        Assert.Contains("От < ISNULL(@До, '20500101')", connection.CommandLog[1]);
        Assert.DoesNotContain("ОтПлан =", connection.CommandLog[1]);
    }

    /// <summary>Существующая услуга обновляется по идентификатору без DELETE.</summary>
    [Fact]
    public async Task UpdateService_UsesIdWithoutDelete()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.SaveSimServiceAsync(new EquipmentSimService
        {
            Id = 15,
            EquipmentId = 7,
            PhoneNumber = "79000000001",
            ServiceTypeId = "_TEST",
            ServiceName = "Test",
            DateFrom = DateTime.Today,
            DateTo = DateTime.Today,
        });

        Assert.Contains("UPDATE vwSIMКартыДопУслугиСотОператора", connection.CommandLog[1]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[1]);
    }

    /// <summary>История восстанавливает поля услуги и читает окончание минус один день.</summary>
    [Fact]
    public async Task GetServices_MapsFieldsAndUsesInclusiveEnd()
    {
        DapperColumnMapper.Initialize();
        var from = new DateTime(2026, 11, 1);
        var to = new DateTime(2026, 11, 17);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодSIMКартыДопУслугиСотОператора", 15),
                ("НомерТелефона", "79000000001"), ("КодТипаДопУслугиСотОператора", "_TEST"),
                ("ДопУслугаСотОператора", "Test"), ("От", from), ("До", to),
                ("СтоимостьВключения", 1m), ("СтоимостьВдень", 2m), ("СтоимостьВмесяц", 3m),
                ("КодОбязательнойУслугиДляКомпании", true), ("ТарифныйПлан", (byte)1),
                ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var services = await service.GetSimServicesAsync(7, true, true);

        var item = Assert.Single(services);
        Assert.Equal(15, item.Id);
        Assert.Equal(to, item.DateTo);
        Assert.Equal(3m, item.MonthlyCost);
        Assert.True(item.IsCompanyRequired);
        Assert.Contains("DATEADD(day, -1, U.До)", connection.CommandLog[0]);
    }
}
