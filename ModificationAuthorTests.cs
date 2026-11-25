using Clayzor.Lib.Entities.DynamicGrid;

namespace Clayzor.Lib.Web.Controls.Tests;

public class ModificationAuthorTests
{
    [Theory]
    [InlineData("ФИО")]
    [InlineData("ИзменилФИО")]
    [InlineData("Изменил ФИО")]
    public void RecognizesAuthorColumns(string name) => Assert.True(ModificationReportData.IsAuthorColumn(name));

    [Theory]
    [InlineData(null, null)]
    [InlineData("-1", null)]
    [InlineData("42", 42)]
    public void UsesOnlyPositiveHiddenAuthorKey(string? raw, int? expected) =>
        Assert.Equal(expected, ModificationReportData.AuthorId(new Dictionary<string, object?> { ["Изменил"] = raw, ["Id"] = 99 }));
    [Theory]
    [InlineData("ФИО")]
    [InlineData("ИзменилФИО")]
    [InlineData("Изменил ФИО")]
    public void UsesResolvedAuthorExceptForOriginalKey(string column)
    {
        var row = new Dictionary<string, object?> { ["Изменил"] = 42, [column] = "SQL label" };
        var page = new ModificationReportPage(["Изменил", column], [row], 1) { Authors = new Dictionary<int, string> { [42] = "Employee FIO" } };
        Assert.Equal("Employee FIO", ModificationReportData.CellText(page, row, column));
        Assert.Equal("SQL label", ModificationReportData.CellText(page with { Columns = [column, "Изменил"] }, row, column));
        row[column] = null;
        Assert.Equal("#42", ModificationReportData.CellText(page with { Authors = new Dictionary<int, string>() }, row, column));
        row["Изменил"] = null;
        Assert.Equal("Автор не указан", ModificationReportData.CellText(page, row, column));
    }
}