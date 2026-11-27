using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using static Clayzor.Lib.Web.Controls.Tests.ModelEquipmentWriteBoundaryTests;
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
        var connection = ModelWriteConnection(Script.Scalar(42));
        var model = new EquipmentModel { EquipmentTypeId = 5, Name = "Created", PartNumber = "PN-1" };

        var id = await model.SaveAsync(CreateDb(connection), @"DOMAIN\fixture", 4, Guid.NewGuid());

        Assert.Equal(42, id);
        Assert.Equal(42, model.Id);
        var sql = Assert.Single(connection.CommandLog, IsWrite);
        Assert.Contains("INSERT INTO МоделиОборудования", sql);
        Assert.Contains("@EquipmentTypeId", sql);
        Assert.Contains("CAST(SCOPE_IDENTITY() AS int)", sql);
    }

    /// <summary>Нулевой или отсутствующий ID после INSERT считается ошибкой, а не успешным сохранением.</summary>
    [Fact]
    public async Task InsertWithoutCreatedId_Throws()
    {
        var connection = ModelWriteConnection(Script.Scalar(0));
        var model = new EquipmentModel { EquipmentTypeId = 5, Name = "Created", PartNumber = "PN-1" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            model.SaveAsync(CreateDb(connection), @"DOMAIN\fixture", 4, Guid.NewGuid()));

        Assert.Contains("не вернула идентификатор", error.Message);
        Assert.Equal(0, model.Id);
    }

    /// <summary>Существующая модель обновляется без INSERT.</summary>
    [Fact]
    public async Task Update_UsesParameterizedUpdate()
    {
        var connection = ModelWriteConnection(Script.Scalar(1), id: 7);
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Updated", PartNumber = "PN-2" };

        var id = await model.SaveAsync(CreateDb(connection), @"DOMAIN\fixture", 4, Guid.NewGuid(), model.CaptureWriteBaseline());

        Assert.Equal(7, id);
        var sql = Assert.Single(connection.CommandLog, IsWrite);
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

    private static DbManager CreateDb(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=test", new FakeSqlErrorHandler(), () => new EquipmentAccountTransactionConnection(connection));
    }

    private static ScriptedConnection ModelWriteConnection(ScriptEntry result, int id = 0)
    {
        var role = Roles(Роли.Специалист_по_инвентаризации_оборудования);
        ScriptEntry[] proposal = id == 0 ? [Type(5), Script.Scalar(0)] : [Type(5), Model(id, 5), Script.Scalar(0)];
        return new ScriptedConnection(new Queue<ScriptEntry>([
            role, Script.Rows(), .. proposal, Script.Rows(), NoPending(), Script.NonQuery(),
            role, .. proposal, result, Script.Scalar(1),
        ]));
    }
}
