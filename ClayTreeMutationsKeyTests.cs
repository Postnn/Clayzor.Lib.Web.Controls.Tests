using Clayzor.Lib.Entities.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты ключа инвалидации кэша мутаций <see cref="ClayTreeMutationsKey"/>.
/// </summary>
public class ClayTreeMutationsKeyTests
{
    /// <summary>Дефолтные Options дают ожидаемые значения ключа.</summary>
    [Fact]
    public void From_Defaults_ExpectedValues()
    {
        var opts = new ClayTreeOptions();

        var key = ClayTreeMutationsKey.From(opts);

        Assert.Null(key.TableName);
        Assert.Null(key.ConnectionStringName);
        Assert.Equal("", key.IdColumn);
        Assert.Equal("Parent", key.ParentColumn);
        Assert.Equal("L", key.LeftColumn);
        Assert.Equal("R", key.RightColumn);
    }

    /// <summary>Смена TableName меняет ключ.</summary>
    [Fact]
    public void From_TableNameChange_ChangesKey()
    {
        var opts = new ClayTreeOptions { TableName = "dbo.TreeA" };
        var key1 = ClayTreeMutationsKey.From(opts);

        opts.TableName = "dbo.TreeB";
        var key2 = ClayTreeMutationsKey.From(opts);

        Assert.NotEqual(key1, key2);
    }

    /// <summary>Пустой TableName эквивалентен null — оба означают DI-путь.</summary>
    [Fact]
    public void From_EmptyTableName_EqualsNullTableName()
    {
        var opts1 = new ClayTreeOptions(); // TableName = null (default)
        var opts2 = new ClayTreeOptions { TableName = "" };

        var key1 = ClayTreeMutationsKey.From(opts1);
        var key2 = ClayTreeMutationsKey.From(opts2);

        Assert.Equal(key1, key2);
    }

    /// <summary>Смена ConnectionStringName меняет ключ.</summary>
    [Fact]
    public void From_ConnectionStringNameChange_ChangesKey()
    {
        var opts = new ClayTreeOptions { ConnectionStringName = "Main" };
        var key1 = ClayTreeMutationsKey.From(opts);

        opts.ConnectionStringName = "Reporting";
        var key2 = ClayTreeMutationsKey.From(opts);

        Assert.NotEqual(key1, key2);
    }

    /// <summary>Смена колонки схемы на том же экземпляре меняет ключ.</summary>
    [Theory]
    [InlineData("IdColumn", "NewId")]
    [InlineData("ParentColumn", "NewParent")]
    [InlineData("LeftColumn", "NewL")]
    [InlineData("RightColumn", "NewR")]
    public void From_SchemaColumnChange_SameInstance_ChangesKey(string columnName, string newValue)
    {
        var opts = new ClayTreeOptions
        {
            Schema = new ClayTreeSchema
            {
                IdColumn = "ID",
                ParentColumn = "Parent",
                LeftColumn = "L",
                RightColumn = "R",
            },
        };
        var key1 = ClayTreeMutationsKey.From(opts);

        // Мутация того же экземпляра схемы — проверка value-based сравнения.
        switch (columnName)
        {
            case "IdColumn": opts.Schema.IdColumn = newValue; break;
            case "ParentColumn": opts.Schema.ParentColumn = newValue; break;
            case "LeftColumn": opts.Schema.LeftColumn = newValue; break;
            case "RightColumn": opts.Schema.RightColumn = newValue; break;
        }
        var key2 = ClayTreeMutationsKey.From(opts);

        Assert.NotEqual(key1, key2);
    }

    /// <summary>Изменение настроек, не влияющих на мутации, не меняет ключ.</summary>
    [Fact]
    public void From_UnrelatedChanges_KeyUnchanged()
    {
        var opts = new ClayTreeOptions
        {
            TableName = "dbo.Tree",
            ConnectionStringName = "Main",
            Schema = new ClayTreeSchema
            {
                IdColumn = "ID",
                ParentColumn = "Parent",
                LeftColumn = "L",
                RightColumn = "R",
            },
        };
        var key1 = ClayTreeMutationsKey.From(opts);

        // Мутация полей, не используемых ClaySqlTreeMutations.
        opts.TreeId = "Other";
        opts.SelectSql = "SELECT * FROM Other";
        opts.HierarchyMode = ClayTreeHierarchyMode.ParentKey;
        opts.RootId = 99;
        opts.OrderBy = "Name";
        opts.Schema.TextColumn = "OtherText";
        opts.Schema.LevelColumn = "OtherLevel";
        opts.Schema.RootParentValue = 1;
        opts.Schema.ExtraColumns = ["ColA", "ColB"];
        opts.EditColumn = "OtherEdit";
        opts.NodePathFunction = "dbo.OtherFn";
        opts.NodePathDirection = ClayTreePathDirection.ChildToParent;

        var key2 = ClayTreeMutationsKey.From(opts);

        Assert.Equal(key1, key2);
    }

    /// <summary>Два ключа с одинаковыми значениями равны.</summary>
    [Fact]
    public void Equals_SameValues_True()
    {
        var opts = new ClayTreeOptions
        {
            TableName = "dbo.Tree",
            ConnectionStringName = "Main",
            Schema = new ClayTreeSchema
            {
                IdColumn = "ID",
                ParentColumn = "Parent",
                LeftColumn = "L",
                RightColumn = "R",
            },
        };

        var key1 = ClayTreeMutationsKey.From(opts);
        var key2 = ClayTreeMutationsKey.From(opts);

        Assert.Equal(key1, key2);
        Assert.True(key1 == key2);
    }
}
