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
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("ЕстьХарактеристикиSIM", (byte)1))), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await service.SaveSimServiceAsync(new EquipmentSimService
        {
            EquipmentId = 7,
            PhoneNumber = "79000000001",
            ServiceTypeId = "_TEST",
            ServiceName = "Test",
            DateFrom = DateTime.Today,
        }, DateTime.Today);

        Assert.Contains("INSERT vwSIMКартыДопУслугиСотОператора", connection.CommandLog[2]);
        Assert.Contains("@От < ISNULL(До, '20500101')", connection.CommandLog[2]);
        Assert.Contains("От < ISNULL(@До, '20500101')", connection.CommandLog[2]);
        Assert.DoesNotContain("ОтПлан =", connection.CommandLog[2]);
    }

    /// <summary>Существующая услуга обновляется по идентификатору без DELETE.</summary>
    [Fact]
    public async Task UpdateService_UsesIdWithoutDelete()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("ЕстьХарактеристикиSIM", (byte)1))), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await service.SaveSimServiceAsync(new EquipmentSimService
        {
            Id = 15,
            EquipmentId = 7,
            PhoneNumber = "79000000001",
            ServiceTypeId = "_TEST",
            ServiceName = "Test",
            DateFrom = DateTime.Today,
            DateTo = DateTime.Today,
        }, DateTime.Today);

        Assert.Contains("UPDATE vwSIMКартыДопУслугиСотОператора", connection.CommandLog[2]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[2]);
    }

    /// <summary>История восстанавливает поля услуги и читает окончание минус один день.</summary>
    [Fact]
    public async Task GetServices_MapsFieldsAndUsesInclusiveEnd()
    {
        DapperColumnMapper.Initialize();
        var from = new DateTime(2026, 11, 1);
        var to = new DateTime(2026, 11, 17);
        var plannedFrom = new DateTime(2026, 10, 20);
        var plannedTo = new DateTime(2026, 11, 20);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодSIMКартыДопУслугиСотОператора", 15),
                ("НомерТелефона", "79000000001"), ("КодТипаДопУслугиСотОператора", "_TEST"),
                ("ДопУслугаСотОператора", "Test"), ("От", from), ("До", to),
                ("ОтПлан", plannedFrom), ("ДоПлан", plannedTo),
                ("СтоимостьВключения", 1m), ("СтоимостьВдень", 2m), ("СтоимостьВмесяц", 3m),
                ("КодОбязательнойУслугиДляКомпании", true), ("УслугуНеобходимоОтключить", (byte)1),
                ("ОбязательнаяУслуга", "Услуга не входит в договор"), ("ТарифныйПлан", (byte)1),
                ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var services = await service.GetSimServicesAsync(7, true, true);

        var item = Assert.Single(services);
        Assert.Equal(15, item.Id);
        Assert.Equal(to, item.DateTo);
        Assert.Equal(plannedFrom, item.PlannedDateFrom);
        Assert.Equal(plannedTo, item.PlannedDateTo);
        Assert.Equal(3m, item.MonthlyCost);
        Assert.True(item.IsCompanyRequired);
        Assert.Equal(1, item.MustBeDisabled);
        Assert.Equal("Услуга не входит в договор", item.GetWarning(DateTime.Today));
        Assert.Contains("DATEADD(day, -1, U.До)", connection.CommandLog[0]);
        Assert.Contains("SIMКартыДопУслугиСотОператораОбязательные R", connection.CommandLog[0]);
        Assert.Contains("CASE WHEN U.КодТипаДопУслугиСотОператора = '_BLOCK'", connection.CommandLog[0]);
    }

    /// <summary>Плановые предупреждения используют точные неравенства legacy.</summary>
    [Fact]
    public void PlannedWarnings_UseLegacyBoundaries()
    {
        var today = new DateTime(2026, 11, 22);
        var service = new EquipmentSimService { PlannedDateFrom = today };
        Assert.False(service.HasOverduePlannedAction(today));
        service.PlannedDateFrom = today.AddDays(-1);
        Assert.True(service.HasOverduePlannedAction(today));
        service.PlannedDateFrom = null;
        service.PlannedDateTo = today;
        Assert.True(service.HasOverduePlannedAction(today));
        service.PlannedDateTo = today.AddDays(1);
        Assert.False(service.HasOverduePlannedAction(today));
    }

    /// <summary>Блокировка учитывает перевод эксклюзивной даты БД во включительную дату UI.</summary>
    [Fact]
    public void ActiveBlock_UsesInclusiveUiEnd()
    {
        var today = new DateTime(2026, 11, 22);
        var service = new EquipmentSimService { ServiceTypeId = "_BLOCK", DateTo = today };
        Assert.True(service.IsActiveBlock(today));
        service.DateTo = today.AddDays(-1);
        Assert.False(service.IsActiveBlock(today));
        service.DateTo = null;
        Assert.True(service.IsActiveBlock(today));
        service.ServiceTypeId = "_TEST";
        Assert.False(service.IsActiveBlock(today));
    }
}
