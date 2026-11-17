using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки SQL-путей сохранения и загрузки модели оборудования.</summary>
public sealed class EquipmentModelPersistenceTests
{
    /// <summary>Создание выполняет INSERT и присваивает возвращённый идентификатор.</summary>
    [Fact]
    public async Task Insert_ReturnsCreatedId()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(42)]));
        var model = new EquipmentModel { EquipmentTypeId = 5, Name = "Created", PartNumber = "PN-1" };

        var id = await model.SaveAsync(CreateDb(connection));

        Assert.Equal(42, id);
        Assert.Equal(42, model.Id);
        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("INSERT INTO МоделиОборудования", sql);
        Assert.Contains("@EquipmentTypeId", sql);
        Assert.Contains("CAST(@@IDENTITY AS int)", sql);
    }

    /// <summary>Существующая модель обновляется без INSERT.</summary>
    [Fact]
    public async Task Update_UsesParameterizedUpdate()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Updated", PartNumber = "PN-2" };

        var id = await model.SaveAsync(CreateDb(connection));

        Assert.Equal(7, id);
        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("UPDATE МоделиОборудования", sql);
        Assert.Contains("WHERE КодМоделиОборудования = @Id", sql);
        Assert.DoesNotContain("INSERT INTO МоделиОборудования", sql);
    }

    /// <summary>Полная выборка восстанавливает характеристики и сведения об изменении.</summary>
    [Fact]
    public async Task GetForEdit_MapsAllCharacteristicTypes()
    {
        DapperColumnMapper.Initialize();
        var changedAt = new DateTime(2026, 11, 17, 12, 30, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодМоделиОборудования", 7), ("МодельОборудования", "Computer"),
                ("КодТипаОборудования", 5), ("КодРесурса", null), ("PN", "PN"),
                ("CPU", "Intel"), ("РазмерМонитора", 0d), ("КодТипаПамяти", 2),
                ("БанковПамяти", 4), ("Мультимедиа", true), ("Net", 1),
                ("НазваниеNet", "LAN"), ("Драйвер", "Driver"), ("Ресурс", "5 years"),
                ("ШаблонТелефона", ""), ("Примечания", "Note"),
                ("Изменил", 3), ("Изменено", changedAt), ("ChangedByName", "Иван Иванов"))),
        ]));

        var model = await EquipmentModel.GetForEditAsync(CreateDb(connection), 7);

        Assert.NotNull(model);
        Assert.Equal("Intel", model.Cpu);
        Assert.Equal(4, model.MemoryBanks);
        Assert.True(model.IsMultimedia);
        Assert.Equal(1, model.NetworkInterfaces);
        Assert.Equal("LAN", model.NetworkName);
        Assert.Equal(3, model.ChangedById);
        Assert.Equal("Иван Иванов", model.ChangedByName);
        Assert.Equal(changedAt, model.ChangedAt);
        Assert.Contains("CONVERT(int, m.Net)", connection.CommandLog.Single());
    }

    /// <summary>Нулевой ресурс означает пустое значение и не вызывает запрос.</summary>
    [Fact]
    public async Task ZeroResource_ReturnsNullWithoutDatabaseCall()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>());
        using var source = new ResourceSelectDataSource(CreateDb(connection));

        var resource = await source.GetByIdAsync(0);

        Assert.Null(resource);
        Assert.Empty(connection.CommandLog);
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
