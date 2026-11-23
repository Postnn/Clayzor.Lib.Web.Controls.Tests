using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки чистых доменных правил Computer/SIM/License EQ8.</summary>
public sealed class Eq8DomainValidationRulesTests
{
    /// <summary>Обе конфигурации требуют парных дат, config1 обязателен при config2/date1.</summary>
    [Fact]
    public void ComputerConfigurationPairs_AreBidirectionalAndOrdered()
    {
        var today = new DateTime(2026, 11, 18);
        var snapshot = new EquipmentComputerSnapshot();
        Assert.Null(ValidateConfiguration(snapshot, today));

        snapshot.Configuration1Id = 1;
        Assert.Contains("дату Стандартной конфигурации 1", ValidateConfiguration(snapshot, today));
        snapshot.Configuration1Date = today;
        Assert.Null(ValidateConfiguration(snapshot, today));

        snapshot.Configuration1Id = null;
        Assert.Contains("Стандартную конфигурацию 1", ValidateConfiguration(snapshot, today));

        snapshot.Configuration1Date = null;
        snapshot.Configuration2Id = 2;
        snapshot.Configuration2Date = today;
        Assert.Contains("Стандартную конфигурацию 1", ValidateConfiguration(snapshot, today));

        snapshot.Configuration1Id = 1;
        snapshot.Configuration1Date = today;
        Assert.Null(ValidateConfiguration(snapshot, today));
        snapshot.Configuration2Date = null;
        Assert.Contains("дату Стандартной конфигурации 2", ValidateConfiguration(snapshot, today));

        snapshot.Configuration2Id = null;
        snapshot.Configuration2Date = today;
        Assert.Contains("Стандартную конфигурацию 2", ValidateConfiguration(snapshot, today));
    }

    /// <summary>Даты стандартных конфигураций не могут быть позже обязательного today.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ComputerConfigurationDates_RejectFuture(int configurationNumber)
    {
        var today = new DateTime(2026, 11, 18);
        var snapshot = new EquipmentComputerSnapshot
        {
            Configuration1Id = 1,
            Configuration1Date = today,
            Configuration2Id = 2,
            Configuration2Date = today,
        };
        if (configurationNumber == 1)
            snapshot.Configuration1Date = today.AddDays(1);
        else
            snapshot.Configuration2Date = today.AddDays(1);

        Assert.Contains("не может быть больше текущей", ValidateConfiguration(snapshot, today));
    }

    /// <summary>Фактические memoryBanks ограничивают RAM; удаление лишнего компонента исправляет draft.</summary>
    [Fact]
    public void ComputerMemoryBanks_RejectsThirdComponentAndAllowsCorrection()
    {
        var today = new DateTime(2026, 11, 18);
        var snapshot = new EquipmentComputerSnapshot { RamComponents = [1024, 1024, 1024] };
        Assert.Contains("2", ValidateConfiguration(snapshot, today, memoryBanks: 2));

        snapshot.RamComponents = [1024, 1024];
        Assert.Null(ValidateConfiguration(snapshot, today, memoryBanks: 2));
        snapshot.RamComponents = [1024, 1024, 1024];
        Assert.Null(ValidateConfiguration(snapshot, today));
    }

    /// <summary>Catalog и custom create/update допустимы; оба пустых и catalog без названия запрещены.</summary>
    [Fact]
    public void SimService_AllowsCatalogAndCustomButRejectsBothEmpty()
    {
        var today = new DateTime(2026, 11, 18);
        var catalog = new EquipmentSimService
        {
            ServiceTypeId = "_TEST",
            ServiceName = "Catalog",
            DateFrom = today,
        };
        Assert.Null(catalog.Validate(today));

        var custom = new EquipmentSimService
        {
            ServiceTypeId = string.Empty,
            ServiceName = "Custom",
            DateFrom = today,
        };
        Assert.Null(custom.Validate(today));

        custom.Id = 15;
        custom.DateTo = today;
        Assert.Null(custom.Validate(today));

        custom.ServiceName = " ";
        Assert.NotNull(custom.Validate(today));
        catalog.ServiceName = string.Empty;
        Assert.Equal("Укажите название услуги.", catalog.Validate(today));
    }

    /// <summary>Custom не отменяет остальные правила фактических дат.</summary>
    [Fact]
    public void SimServiceCustom_KeepsDateRules()
    {
        var today = new DateTime(2026, 11, 18);
        var custom = new EquipmentSimService
        {
            ServiceName = "Custom",
            DateFrom = today.AddDays(1),
        };
        Assert.Equal("Фактические даты не могут быть в будущем.", custom.Validate(today));
        custom.DateFrom = today;
        custom.DateTo = today.AddDays(-1);
        Assert.Equal("Дата подключения не может быть позже даты прекращения.", custom.Validate(today));
    }

    /// <summary>Отрицательные количества лицензии отклоняются, нулевой ServerCount остаётся допустимым.</summary>
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void License_RejectsNegativeCounts(int serverCount, int clientCount)
    {
        var type = new LicenseType { Id = 1, Resale = 1 };
        var license = new EquipmentLicense
        {
            LicenseTypeId = 1,
            ServerCount = serverCount,
            ClientCount = clientCount,
        };
        Assert.Contains("не может быть отрицатель", license.Validate(type));
    }

    /// <summary>Существующие null/zero правила по типу не изменены.</summary>
    [Fact]
    public void License_PreservesZeroAndRequiredRules()
    {
        var type = new LicenseType { Id = 1, Resale = 1 };
        var license = new EquipmentLicense { LicenseTypeId = 1, ServerCount = 0, ClientCount = 1 };
        Assert.Null(license.Validate(type));
        license.ServerCount = null;
        Assert.Equal("Укажите количество серверов.", license.Validate(type));
        license.ServerCount = 0;
        license.ClientCount = 0;
        Assert.Equal("Количество клиентов должно быть ненулевым.", license.Validate(type));
    }

    private static string? ValidateConfiguration(
        EquipmentComputerSnapshot snapshot,
        DateTime today,
        int? memoryBanks = null)
    {
        var method = typeof(EquipmentComputerSnapshot).GetMethod(
            "ValidateConfiguration", [typeof(DateTime), typeof(int?)]);
        Assert.NotNull(method);
        return (string?)method.Invoke(snapshot, [today, memoryBanks]);
    }
}
