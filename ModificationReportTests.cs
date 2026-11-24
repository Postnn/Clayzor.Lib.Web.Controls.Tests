using Clayzor.Lib.Entities.DynamicGrid;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class ModificationReportTests
{
    private static readonly DateTime Sunday = new(2024, 2, 25);

    [Theory]
    [InlineData(ClayPeriodKind.Day, 2, 25, 2, 25)]
    [InlineData(ClayPeriodKind.Week, 2, 19, 2, 25)]
    [InlineData(ClayPeriodKind.Month, 2, 1, 2, 29)]
    [InlineData(ClayPeriodKind.Quarter, 1, 1, 3, 31)]
    [InlineData(ClayPeriodKind.Year, 1, 1, 12, 31)]
    public void ExpandsCalendarPeriod(ClayPeriodKind kind, int startMonth, int startDay, int endMonth, int endDay)
    {
        var selected = new ClayPeriod(ClayPeriodKind.Day, Sunday, Sunday).Select(kind, Sunday);
        Assert.Equal(new DateTime(2024, startMonth, startDay), selected.From);
        Assert.Equal(new DateTime(2024, endMonth, endDay), selected.To);
    }

    [Fact]
    public void MovesAcrossYearAndChangesToCustomAfterEditingAnEnd()
    {
        var now = new DateTime(2024, 12, 2);
        var month = new ClayPeriod(ClayPeriodKind.Day, now, now).Select(ClayPeriodKind.Month, now);
        var january = month.Move(1);
        Assert.Equal(new DateTime(2025, 1, 31), january.To);
        var shortInterval = january.End(now);
        Assert.Equal(new ClayPeriod(ClayPeriodKind.Custom, now, now), shortInterval);
        Assert.False(shortInterval.Start(null).IsComplete);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SELECT id INTO dbo.Bad FROM dbo.Source")]
    [InlineData("SELECT * FROM OPENQUERY(Remote, 'SELECT id FROM T')")]
    [InlineData("SELECT * FROM Remote.Database.dbo.Source")]
    [InlineData("SELECT 1) p; DELETE FROM dbo.Source; SELECT * FROM (SELECT 1 AS a")]
    public void RejectsDefinitionsOutsideTheReadContract(string source) =>
        Assert.Throws<InvalidDataException>(() => ModificationReportData.ValidateSelect(source));

    [Fact]
    public void KeepsSqlLiteralAndRemovesOptionalStatementTerminator() =>
        Assert.Equal("SELECT 'a; b' AS col", ModificationReportData.ValidateSelect("  SELECT 'a; b' AS col;  "));

    [Fact]
    public void CardReferencesPreserveQueryAndFragmentAndEncodeTheKey()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["URI_card"] = "/Equipment/Card?mode=1#data"
        }).Build();
        var result = InventoryMenuUrlBuilder.BuildCardLink("@URI_card", "A+B (&)", config, "http://localhost/Reports/Modifications");
        Assert.Equal("http://localhost/Equipment/Card?mode=1&id=A%2bB+(%26)#data", result.Href);
        Assert.Null(InventoryMenuUrlBuilder.BuildCardLink("javascript:bad", "id", config, "http://localhost/").Href);
    }
}
