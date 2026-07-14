using Clayzor.Lib.Entities.DynamicGrid;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;
using Microsoft.Extensions.Configuration;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты опций динамического грида <see cref="ClayGridDynamicOptions"/>
/// и маппинга схемы <see cref="ClayGridSchemaMap"/>.
/// </summary>
public class OptionsBindingTests
{
    /// <summary>
    /// Байнд из in-memory IConfiguration заполняет все поля опций.
    /// </summary>
    [Fact]
    public void Bind_FromInMemoryConfig_AllFieldsPopulated()
    {
        var dict = new Dictionary<string, string?>
        {
            ["ClayGrid:Dynamic:ConnectionStringName"] = "Main",
            ["ClayGrid:Dynamic:SettingsTable"] = "CustSettings",
            ["ClayGrid:Dynamic:ColumnsTable"] = "CustColumns",
            ["ClayGrid:Dynamic:UserParamsTable"] = "CustUserParams",
            ["ClayGrid:Dynamic:GridIdQueryParam"] = "gid",
            ["ClayGrid:Dynamic:ColumnsParamPrefix"] = "c",
            ["ClayGrid:Dynamic:FilterParamPrefix"] = "f",
            ["ClayGrid:Dynamic:GroupingParamPrefix"] = "g",
            ["ClayGrid:Dynamic:SortingParamPrefix"] = "s",
            ["ClayGrid:Dynamic:PageSizeParamPrefix"] = "p",
            ["ClayGrid:Dynamic:ClientIdQueryParam"] = "cid"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var opts = new ClayGridDynamicOptions();
        config.GetSection("ClayGrid:Dynamic").Bind(opts);

        Assert.Equal("Main", opts.ConnectionStringName);
        Assert.Equal("CustSettings", opts.SettingsTable);
        Assert.Equal("CustColumns", opts.ColumnsTable);
        Assert.Equal("CustUserParams", opts.UserParamsTable);
        Assert.Equal("gid", opts.GridIdQueryParam);
        Assert.Equal("c", opts.ColumnsParamPrefix);
        Assert.Equal("f", opts.FilterParamPrefix);
        Assert.Equal("g", opts.GroupingParamPrefix);
        Assert.Equal("s", opts.SortingParamPrefix);
        Assert.Equal("p", opts.PageSizeParamPrefix);
        Assert.Equal("cid", opts.ClientIdQueryParam);
    }

    /// <summary>Дефолты схемы: Settings.Title == "Запрос".</summary>
    [Fact]
    public void Schema_Defaults_SettingsTitle()
    {
        var map = new ClayGridSchemaMap();
        Assert.Equal("Запрос", map.Settings.Title);
    }

    /// <summary>Дефолты схемы: Columns.Type == "Тип".</summary>
    [Fact]
    public void Schema_Defaults_ColumnsType()
    {
        var map = new ClayGridSchemaMap();
        Assert.Equal("Тип", map.Columns.Type);
    }

    /// <summary>Дефолты схемы: UserParams.Name == "Параметр".</summary>
    [Fact]
    public void Schema_Defaults_UserParamsName()
    {
        var map = new ClayGridSchemaMap();
        Assert.Equal("Параметр", map.UserParams.Name);
    }

    /// <summary>Validate() при пустом ConnectionStringName бросает InvalidOperationException.</summary>
    [Fact]
    public void Validate_EmptyConnectionStringName_ThrowsInvalidOperationException()
    {
        var opts = new ClayGridDynamicOptions
        {
            ConnectionStringName = "",
            SettingsTable = "T1",
            ColumnsTable = "T2",
            UserParamsTable = "T3"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("ConnectionStringName", ex.Message);
    }

    /// <summary>Validate() при пустом SettingsTable бросает InvalidOperationException.</summary>
    [Fact]
    public void Validate_EmptySettingsTable_ThrowsInvalidOperationException()
    {
        var opts = new ClayGridDynamicOptions
        {
            ConnectionStringName = "Main",
            SettingsTable = "",
            ColumnsTable = "T2",
            UserParamsTable = "T3"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("SettingsTable", ex.Message);
    }

    /// <summary>Validate() при заполненных обязательных полях не бросает исключение.</summary>
    [Fact]
    public void Validate_AllFieldsSet_DoesNotThrow()
    {
        var opts = new ClayGridDynamicOptions
        {
            ConnectionStringName = "Main",
            SettingsTable = "T1",
            ColumnsTable = "T2",
            UserParamsTable = "T3"
        };

        var ex = Record.Exception(() => opts.Validate());
        Assert.Null(ex);
    }
}
