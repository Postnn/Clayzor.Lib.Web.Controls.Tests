using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Documents;
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

    /// <summary>Телефонный источник использует legacy-функцию и параметризованные прикладные фильтры.</summary>
    [Fact]
    public async Task PhoneSearch_UsesLegacyFiltersAndExcludesCurrentIds()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new PhoneNumberSelectDataSource(CreateDb(connection))
        {
            LocationId = 55, EmployeeId = 4, EquipmentTypeId = 30, PhoneIdBase = 10, EquipmentId = 7,
            ExcludedPhoneIds = [11, 12, 11],
        };

        await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "101", Limit = 8 });

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("fn_НомераДляТелАппаратов", sql);
        Assert.Contains("NOT IN (@ExcludedPhoneId0, @ExcludedPhoneId1)", sql);
        Assert.DoesNotContain("NOT IN (11", sql);
        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(55, parameters["LocationId"]);
        Assert.Equal(4, parameters["EmployeeId"]);
        Assert.Equal(30, parameters["EquipmentTypeId"]);
        Assert.Equal(10, parameters["PhoneIdBase"]);
        Assert.Equal(7, parameters["EquipmentId"]);
        Assert.Equal(2, parameters.Count(item => item.Key.StartsWith("ExcludedPhoneId")));
    }

    /// <summary>Документы списания фильтруются по лицу и точному legacy-типу 2365.</summary>
    [Fact]
    public async Task DocumentSearch_UsesPersonAndDocumentTypeFilters()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new DocumentSelectDataSource(CreateDb(connection)) { PersonId = 1, DocumentTypeId = 2365 };

        await source.SearchAsync(new ClaySelectSearchRequest { Limit = 8 });

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("T0.КодТипаДокумента = @DocumentTypeId", sql);
        Assert.Contains("P.КодЛица = @PersonId", sql);
        Assert.Equal(2365, Assert.Single(connection.ParameterLog)["DocumentTypeId"]);
    }

    /// <summary>Выбор UPS применяет серверный фильтр текущего офиса и исключает склад.</summary>
    [Fact]
    public async Task UpsSearch_UsesLocationInOfficeFilter()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new UpsEquipmentSelectDataSource(CreateDb(connection), true) { LocationId = 55 };

        await source.SearchAsync(new ClaySelectSearchRequest { Limit = 8 });

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("@LocationId IS NULL OR EXISTS", sql);
        Assert.Contains("ParentLocation.Офис = 1", sql);
        Assert.Contains("ChildLocation.РабочееМесто <> 4", sql);
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
