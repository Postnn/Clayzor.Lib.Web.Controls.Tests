using System.Data;
using Clayzor.Lib.Entities.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.DataSources;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты построителя SQL для дерева <see cref="ClayTreeSqlBuilder"/>
/// и вспомогательных методов <see cref="ClaySqlTreeDataSource"/>.
/// </summary>
public class ClayTreeSqlBuilderTests
{
    private static ClayTreeSource CreateNestedSetSource(bool withLevelColumn)
    {
        var schema = new ClayTreeSchema
        {
            IdColumn    = "Id",
            TextColumn  = "Name",
            LeftColumn  = "L",
            RightColumn = "R",
            LevelColumn = withLevelColumn ? "Level" : null,
        };
        return new ClayTreeSource("SELECT Id, Name, L, R FROM Tree", ClayTreeHierarchyMode.NestedSet, schema);
    }

    /// <summary>NestedSet без LevelColumn: не-корневой SQL содержит NOT EXISTS.</summary>
    [Fact]
    public void BuildNestedSetSql_NonRoot_WithoutLevelColumn_ContainsNotExists()
    {
        var src = CreateNestedSetSource(withLevelColumn: false);
        var sql = ClayTreeSqlBuilder.BuildLevelSql(src, isRoot: false);

        Assert.Contains("NOT EXISTS", sql);
        Assert.DoesNotContain("[Level]", sql);
        Assert.Contains("@left", sql);
        Assert.Contains("@right", sql);
    }

    /// <summary>NestedSet с LevelColumn: не-корневой SQL содержит = @level + 1, без NOT EXISTS.</summary>
    [Fact]
    public void BuildNestedSetSql_NonRoot_WithLevelColumn_UsesLevelPredicate()
    {
        var src = CreateNestedSetSource(withLevelColumn: true);
        var sql = ClayTreeSqlBuilder.BuildLevelSql(src, isRoot: false);

        Assert.Contains("= @level + 1", sql);
        Assert.DoesNotContain("NOT EXISTS", sql);
    }

    // ── CTF4: ToKey ──────────────────────────────────────────────────────────

    /// <summary>ToKey(null) → "".</summary>
    [Fact]
    public void ToKey_Null_ReturnsEmpty()
    {
        Assert.Equal("", ClaySqlTreeDataSource.ToKey(null));
    }

    /// <summary>ToKey(DBNull.Value) → "".</summary>
    [Fact]
    public void ToKey_DBNull_ReturnsEmpty()
    {
        Assert.Equal("", ClaySqlTreeDataSource.ToKey(DBNull.Value));
    }

    /// <summary>ToKey(42) → "42".</summary>
    [Fact]
    public void ToKey_Int_ReturnsString()
    {
        Assert.Equal("42", ClaySqlTreeDataSource.ToKey(42));
    }

    /// <summary>ToKey("abc") → "abc".</summary>
    [Fact]
    public void ToKey_String_ReturnsSame()
    {
        Assert.Equal("abc", ClaySqlTreeDataSource.ToKey("abc"));
    }
}
