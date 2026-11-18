using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Проверяет ограничение выдачи и прикладные фильтры общих источников ClaySelect.
/// </summary>
public sealed class ClaySelectDataSourceTests
{
    /// <summary>
    /// Источник типов возвращает Limit элементов и вычисляет HasMore по дополнительной строке.
    /// </summary>
    [Fact]
    public async Task EquipmentTypeSearch_UsesLimitPlusOneContract()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(
                Script.Row(("КодТипаОборудования", 1), ("ТипОборудования", "Ноутбук")),
                Script.Row(("КодТипаОборудования", 2), ("ТипОборудования", "Монитор")),
                Script.Row(("КодТипаОборудования", 3), ("ТипОборудования", "Принтер"))),
        ]));
        var source = new EquipmentTypeSelectDataSource(CreateDb(connection));

        var result = await source.SearchAsync(new ClaySelectSearchRequest { Limit = 2 });

        Assert.Equal(2, result.Items.Count);
        Assert.True(result.HasMore);
        Assert.Equal("Ноутбук", result.Items[0].Name);
    }

    /// <summary>
    /// До выбора типа источник моделей выполняет серверный поиск без фильтра типа.
    /// </summary>
    [Fact]
    public async Task EquipmentModelWithoutType_SearchesAllModels()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодМоделиОборудования", 945), ("МодельОборудования", "Model"),
                ("КодТипаОборудования", 30), ("Net", 2), ("ТипПамяти", "DDR4"))),
        ]));
        var source = new EquipmentModelSelectDataSource(CreateDb(connection));

        var result = await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "model", Limit = 8 });

        var model = Assert.Single(result.Items);
        Assert.Equal(945, model.Id);
        Assert.Equal(30, model.EquipmentTypeId);
        Assert.Equal(2, model.NetworkInterfaces);
        Assert.Equal("DDR4", model.MemoryTypeName);
        Assert.Contains("@EquipmentTypeId IS NULL", Assert.Single(connection.CommandLog));
    }

    /// <summary>
    /// Источник сотрудников применяет Limit и восстанавливает английское имя.
    /// </summary>
    [Fact]
    public async Task EmployeeSearch_AppliesLimitAndMapsColumns()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(
                Script.Row(("КодСотрудника", 10), ("Сотрудник", "Иван Иванов"), ("Employee", "Ivan Ivanov")),
                Script.Row(("КодСотрудника", 11), ("Сотрудник", "Пётр Петров"), ("Employee", "Petr Petrov"))),
        ]));
        var source = new EmployeeSelectDataSource(CreateDb(connection));

        var result = await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "И", Limit = 1 });

        Assert.Single(result.Items);
        Assert.True(result.HasMore);
        Assert.Equal(10, result.Items[0].Id);
        Assert.Equal("Ivan Ivanov", result.Items[0].NameEn);
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
