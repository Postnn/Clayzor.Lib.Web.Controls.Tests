using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки дневных снимков и компонентов конфигурации компьютера.</summary>
public sealed class EquipmentComputerPersistenceTests
{
    /// <summary>Пустые строки и дробные HD разбираются без потери значений.</summary>
    [Fact]
    public void Parse_HandlesEmptyAndDecimalValues()
    {
        Assert.Empty(EquipmentComputerDetail.Parse(null));
        Assert.Empty(EquipmentComputerDetail.Parse(" "));
        Assert.Equal([100.5m, 200.25m], EquipmentComputerDetail.Parse("100.5+200.25"));
    }

    /// <summary>Сериализация удаляет нулевые хвосты, а сумма вычисляется из компонентов.</summary>
    [Fact]
    public void Serialize_TrimsZeroTailsAndSumsComponents()
    {
        var values = new[] { 100.5m, 200.25m, 0m, 0m };

        Assert.Equal("100.5+200.25", EquipmentComputerDetail.Serialize(values, true));
        Assert.Equal(300.75m, EquipmentComputerDetail.Sum(values));
        Assert.Equal(string.Empty, EquipmentComputerDetail.Serialize([0m, 0m], false));
    }

    /// <summary>RAM и SSD не принимают дробные или отрицательные значения.</summary>
    [Fact]
    public void SerializeInteger_RejectsInvalidComponents()
    {
        Assert.Throws<FormatException>(() => EquipmentComputerDetail.Serialize([1.5m], false));
        Assert.Throws<FormatException>(() => EquipmentComputerDetail.Parse("-1+2"));
    }

    /// <summary>UPSERT получает вычисленные итоги и сериализованные компоненты.</summary>
    [Fact]
    public async Task SaveComputer_ComputesTotalsAndUsesDailyUpsert()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(
                ("ModelId", 5), ("EquipmentTypeId", 1),
                ("HasComputerCharacteristics", (byte)1), ("MemoryBanks", 3))),
            Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());
        var snapshot = new EquipmentComputerSnapshot
        {
            EquipmentId = 7,
            Date = new DateTime(2026, 11, 18, 15, 30, 0),
            RamComponents = [1024, 2048, 0],
            HdComponents = [100.5m, 200.25m, 0],
            SsdComponents = [128, 0],
            License = "CLAY_TEST",
        };

        await service.SaveComputerAsync(snapshot, snapshot.Date.Date);

        Assert.Equal(3072, snapshot.RamMb);
        Assert.Equal("1024+2048", snapshot.RamDetail);
        Assert.Equal(300.75f, snapshot.HdGb);
        Assert.Equal("100.5+200.25", snapshot.HdDetail);
        Assert.Equal(128, snapshot.SsdGb);
        Assert.Equal("128", snapshot.SsdDetail);
        Assert.Contains("IF EXISTS", connection.CommandLog[2]);
        Assert.Contains("INSERT Компьютеры", connection.CommandLog[2]);
        Assert.Equal(DBNull.Value, connection.ParameterLog[2]["ИсходноеИзменено"]);
    }

    /// <summary>История восстанавливает поля и компоненты последнего снимка.</summary>
    [Fact]
    public async Task GetComputerHistory_MapsAndParsesDetails()
    {
        DapperColumnMapper.Initialize();
        var date = new DateTime(2026, 11, 18);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("С", date), ("RAM", 3072),
                ("RAMDetail", "1024+2048"), ("HD", 300.75f), ("HDDetail", "100.5+200.25"),
                ("SSD", 128), ("SSDDetail", "128"), ("Лицензия", "license"),
                ("Примечания", "note"), ("ИзменилФИО", "Editor"), ("Конфигурация1", "Standard"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var history = await service.GetComputerHistoryAsync(7);

        var snapshot = Assert.Single(history);
        Assert.Equal(date, snapshot.Date);
        Assert.Equal([1024m, 2048m], snapshot.RamComponents);
        Assert.Equal([100.5m, 200.25m], snapshot.HdComponents);
        Assert.Equal([128m], snapshot.SsdComponents);
        Assert.Equal("Editor", snapshot.ChangedByName);
        Assert.Equal("Standard", snapshot.Configuration1Name);
    }
}
