using Clayzor.Lib.Entities.DynamicGrid;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты мапперов определения и колонок динамического грида
/// <see cref="ClayGridDefinitionData"/>.
/// </summary>
public class DefinitionMapperTests
{
    private static ClayGridSchemaMap DefaultSchema => new();

    /// <summary>
    /// MapDefinition на словаре строки → все поля ClayGridDefinition верные.
    /// </summary>
    [Fact]
    public void MapDefinition_FullRow_AllFieldsCorrect()
    {
        var s = DefaultSchema;
        var row = new Dictionary<string, object?>
        {
            [s.Settings.GridId]   = 140,
            [s.Settings.Title]    = "Медицинские исследования",
            [s.Settings.Icon]     = null,
            [s.Settings.Sql]      = "SELECT КодИсследования, Название FROM Исследования",
            [s.Settings.Id]       = "КодИсследования",
            [s.Settings.IdName]   = "Название",
            [s.Settings.EditForm] = "/medical/edit",
            [s.Settings.NewForm]  = "/medical/new",
            [s.Settings.SqlDelete]= (string?)null
        };

        var def = ClayGridDefinitionData.MapDefinition(row, s);

        Assert.Equal(140, def.GridId);
        Assert.Equal("Медицинские исследования", def.Title);
        Assert.Null(def.IconUrl);
        Assert.Equal("SELECT КодИсследования, Название FROM Исследования", def.Sql);
        Assert.Equal("КодИсследования", def.IdColumn);
        Assert.Equal("Название", def.IdNameColumn);
        Assert.Equal("/medical/edit", def.EditForm);
        Assert.Equal("/medical/new", def.NewForm);
        Assert.Null(def.SqlDelete);
    }

    /// <summary>
    /// MapColumn с Порядок=0 → Order==0 (не отброшено), Type==7.
    /// </summary>
    [Fact]
    public void MapColumn_OrderZero_NotDropped()
    {
        var s = DefaultSchema;
        var row = new Dictionary<string, object?>
        {
            [s.Columns.ColumnId] = 1005,
            [s.Columns.GridId]   = 140,
            [s.Columns.Column]   = "Активно",
            [s.Columns.Header]   = "Активно",
            [s.Columns.UrlKey]   = "active",
            [s.Columns.Order]    = 0,
            [s.Columns.Format]   = "Активно=1",
            [s.Columns.Type]     = 7
        };

        var col = ClayGridDefinitionData.MapColumn(row, s);

        Assert.Equal(1005, col.ColumnId);
        Assert.Equal(0, col.Order);
        Assert.Equal(7, col.Type);
        Assert.Equal("Активно", col.Column);
    }

    /// <summary>
    /// MapColumn с NULL Порядок → Order==null.
    /// </summary>
    [Fact]
    public void MapColumn_NullOrder_ReturnsNull()
    {
        var s = DefaultSchema;
        var row = new Dictionary<string, object?>
        {
            [s.Columns.ColumnId] = 1001,
            [s.Columns.GridId]   = 140,
            [s.Columns.Column]   = "КодИсследования",
            [s.Columns.Header]   = "№",
            [s.Columns.UrlKey]   = "id",
            [s.Columns.Order]    = null!,
            [s.Columns.Format]   = null!,
            [s.Columns.Type]     = 1
        };

        var col = ClayGridDefinitionData.MapColumn(row, s);

        Assert.Null(col.Order);
        Assert.Equal(1, col.Type);
    }

    /// <summary>
    /// BuildGridSql содержит имена из схемы в [], параметр @gridId.
    /// </summary>
    [Fact]
    public void BuildGridSql_UsesSchemaNames_AndGridIdParam()
    {
        var s = DefaultSchema;
        var sql = ClayGridDefinitionData.BuildGridSql("MySettings", s);

        Assert.Contains("[КодЗапроса]", sql);
        Assert.Contains("[Запрос]", sql);
        Assert.Contains("[SQL]", sql);
        Assert.Contains("[MySettings]", sql);
        Assert.Contains("@gridId", sql);
    }

    /// <summary>
    /// BuildColumnsSql содержит имена из схемы в [], параметр @gridId, ORDER BY.
    /// </summary>
    [Fact]
    public void BuildColumnsSql_UsesSchemaNames_AndOrderBy()
    {
        var s = DefaultSchema;
        var sql = ClayGridDefinitionData.BuildColumnsSql("MyColumns", s);

        Assert.Contains("[КодКолонки]", sql);
        Assert.Contains("[ЗаголовокКолонки]", sql);
        Assert.Contains("[Порядок]", sql);
        Assert.Contains("[MyColumns]", sql);
        Assert.Contains("@gridId", sql);
        Assert.Contains("ORDER BY", sql);
    }

    /// <summary>
    /// MapColumn с изменённой схемой (Header:"Caption") читает значение из "Caption".
    /// </summary>
    [Fact]
    public void MapColumn_CustomSchemaHeader_ReadsCustomKey()
    {
        var s = new ClayGridSchemaMap();
        s.Columns.Header = "Caption";
        var row = new Dictionary<string, object?>
        {
            [s.Columns.ColumnId] = 1001,
            [s.Columns.GridId]   = 140,
            [s.Columns.Column]   = "КодИсследования",
            ["Caption"]           = "ТестовыйЗаголовок",
            [s.Columns.UrlKey]   = "id",
            [s.Columns.Order]    = 1,
            [s.Columns.Format]   = null!,
            [s.Columns.Type]     = 1
        };

        var col = ClayGridDefinitionData.MapColumn(row, s);

        Assert.Equal("ТестовыйЗаголовок", col.Header);
    }

    /// <summary>
    /// MapColumn: отсутствующий Тип в строке → Type==0 (дефолт).
    /// </summary>
    [Fact]
    public void MapColumn_MissingType_DefaultsToZero()
    {
        var s = DefaultSchema;
        var row = new Dictionary<string, object?>
        {
            [s.Columns.ColumnId] = 1001,
            [s.Columns.GridId]   = 140,
            [s.Columns.Column]   = "КодИсследования",
            [s.Columns.Header]   = "№",
            [s.Columns.UrlKey]   = "id",
            [s.Columns.Order]    = 1,
            // Тип отсутствует
        };

        var col = ClayGridDefinitionData.MapColumn(row, s);

        Assert.Equal(0, col.Type);
    }
}
