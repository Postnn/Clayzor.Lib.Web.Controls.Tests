using System.Text;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;
using Kesco.App.Web.Inventory.Components.Pages.Default;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class FinderTelTests
{
    [Theory]
    [InlineData(null, true, null)]
    [InlineData("0", true, null)]
    [InlineData("00", true, 0)]
    [InlineData("019", true, 19)]
    [InlineData("", false, null)]
    [InlineData("-1", false, null)]
    [InlineData("2147483648", false, null)]
    [InlineData("1 AND 1=1", false, null)]
    public void ValidatesOptionalLegacyIdentifier(string? input, bool valid, int? id)
    {
        Assert.Equal(valid, PhoneFinderRoutes.TryId(input, out var result));
        Assert.Equal(id, result);
    }

    [Fact]
    public void KeepsContextAndParameterizesExclusionsForTheCompleteProjection()
    {
        var (sql, parameters) = PhoneFinderData.BuildTelQuery(new(1, 2, 3, 4, 5), [71, 92, 71]);
        Assert.Contains(SQLQueries.PhoneNumberCandidateSource, sql);
        Assert.Contains("NOT IN (@ExcludedPhoneId0, @ExcludedPhoneId1)", sql);
        Assert.Equal(71, parameters.Get<int>("ExcludedPhoneId0"));
        Assert.Equal(92, parameters.Get<int>("ExcludedPhoneId1"));
    }

    [Fact]
    public void CompactLookupInsertsExclusionBeforeTheOrderBy()
    {
        var sql = new StringBuilder(SQLQueries.SELECT_ТелефонныеНомераДляВыбора);
        PhoneNumberQuery.AddExcludedIds(sql, new DynamicParameters(), [27]);
        Assert.EndsWith("ORDER BY Телефон", sql.ToString());
        Assert.Contains("NOT IN (@ExcludedPhoneId0)", sql.ToString());
    }

    [Theory]
    [InlineData("17,37,17", true)]
    [InlineData("17,0", false)]
    [InlineData("17,", false)]
    [InlineData("11,2147483648", false)]
    public void ValidatesAndDeduplicatesExclusions(string input, bool valid)
    {
        Assert.Equal(valid, PhoneFinderRoutes.TryExclusions(input, out var ids));
        Assert.Equal(valid ? [17, 37] : [], ids);
    }
}
