using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Phones;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки итоговых SQL-запросов телефонных lookup EQ8/G0.3.</summary>
public sealed class Eq8PhoneLookupQueryTests
{
    /// <summary>Исключения номеров параметризуются, дедуплицируются и остаются внутри WHERE.</summary>
    [Theory]
    [InlineData(new int[0], new string[0])]
    [InlineData(new[] { 7 }, new[] { "@ExcludedPhoneId0" })]
    [InlineData(new[] { 7, 9, 12 }, new[] { "@ExcludedPhoneId0", "@ExcludedPhoneId1", "@ExcludedPhoneId2" })]
    [InlineData(new[] { 7, 7, 9, 7, 9 }, new[] { "@ExcludedPhoneId0", "@ExcludedPhoneId1" })]
    public async Task PhoneNumberSearch_PlacesDistinctExclusionsBeforeOrderBy(
        int[] excludedIds,
        string[] expectedParameterNames)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new PhoneNumberSelectDataSource(CreateDb(connection))
        {
            LocationId = 11,
            EmployeeId = 22,
            EquipmentTypeId = 33,
            PhoneIdBase = 44,
            EquipmentId = 55,
            ExcludedPhoneIds = excludedIds,
        };

        await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "123", Limit = 8 });

        var sql = Assert.Single(connection.CommandLog);
        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(11, parameters["LocationId"]);
        Assert.Equal(22, parameters["EmployeeId"]);
        Assert.Equal(33, parameters["EquipmentTypeId"]);
        Assert.Equal(44, parameters["PhoneIdBase"]);
        Assert.Equal(55, parameters["EquipmentId"]);
        Assert.Equal("123", parameters["Search"]);
        Assert.Equal(9, parameters["LimitPlusOne"]);
        Assert.Contains("SELECT TOP (@LimitPlusOne)", sql);
        Assert.Contains("dbo.fn_НомераДляТелАппаратов(@LocationId, @EmployeeId, @EquipmentTypeId, @PhoneIdBase, @EquipmentId)", sql);

        var orderIndex = sql.LastIndexOf("ORDER BY Телефон", StringComparison.OrdinalIgnoreCase);
        Assert.True(orderIndex > 0);
        Assert.Equal("Телефон", sql[(orderIndex + "ORDER BY".Length)..].Trim());
        var actualNames = parameters.Keys
            .Where(name => name.StartsWith("ExcludedPhoneId", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(expectedParameterNames.Select(name => name.TrimStart('@')), actualNames);

        if (expectedParameterNames.Length == 0)
        {
            Assert.Equal(Normalize(SQLQueries.SELECT_ТелефонныеНомераДляВыбора), Normalize(sql));
            return;
        }

        var predicate = "T0.КодТелефонногоНомера NOT IN (" + string.Join(", ", expectedParameterNames) + ")";
        var predicateIndex = sql.IndexOf(predicate, StringComparison.Ordinal);
        Assert.True(predicateIndex > sql.IndexOf("WHERE", StringComparison.Ordinal));
        Assert.True(predicateIndex < orderIndex);
        for (var index = 0; index < expectedParameterNames.Length; index++)
            Assert.Equal(excludedIds.Distinct().ElementAt(index), parameters[expectedParameterNames[index].TrimStart('@')]);
    }

    /// <summary>Поиск оборудования группирует все OR-ветви до AND-исключений для любого текста поиска.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Model")]
    [InlineData("SN123")]
    [InlineData("Location")]
    public async Task PhoneEquipmentSearch_GroupsSearchOrBranchesBeforeExclusions(string search)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var source = new PhoneEquipmentSelectDataSource(CreateDb(connection))
        {
            PhoneTypeId = 2,
            StationId = 3,
            SocketId = 4,
            EmployeeId = 5,
            PhoneId = 6,
            ExcludedKeys = [new PhoneEquipmentKey(7, 70), new PhoneEquipmentKey(7, 71), new PhoneEquipmentKey(8, 80)],
        };

        await source.SearchAsync(new ClaySelectSearchRequest { SearchText = search, Limit = 20 });

        var sql = Normalize(Assert.Single(connection.CommandLog));
        const string expectedSearch =
            "WHERE (@Search = '' OR F.МодельОборудования LIKE '%' + @Search + '%' " +
            "OR F.SN LIKE '%' + @Search + '%' OR F.РасположениеPath LIKE '%' + @Search + '%')";
        var searchIndex = sql.IndexOf(expectedSearch, StringComparison.Ordinal);
        var firstExclusion = sql.IndexOf("AND F.КодОборудования <> @ExcludedEquipment0", StringComparison.Ordinal);
        var secondExclusion = sql.IndexOf("AND F.КодОборудования <> @ExcludedEquipment1", StringComparison.Ordinal);
        var orderIndex = sql.LastIndexOf("ORDER BY Название", StringComparison.Ordinal);
        Assert.True(searchIndex >= 0);
        Assert.True(firstExclusion > searchIndex + expectedSearch.Length);
        Assert.True(secondExclusion > firstExclusion);
        Assert.True(orderIndex > secondExclusion);

        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(search, parameters["Search"]);
        Assert.Equal(21, parameters["LimitPlusOne"]);
        Assert.Equal(2, parameters["PhoneTypeId"]);
        Assert.Equal(3, parameters["StationId"]);
        Assert.Equal(4, parameters["SocketId"]);
        Assert.Equal(5, parameters["EmployeeId"]);
        Assert.Equal(6, parameters["PhoneId"]);
        Assert.Equal(7, parameters["ExcludedEquipment0"]);
        Assert.Equal(8, parameters["ExcludedEquipment1"]);
        Assert.DoesNotContain("ExcludedEquipment2", parameters.Keys);
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
