using System.Data;
using Clayzor.Lib.Entities.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.DataSources;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;

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

    // ── CTP: кейсет-пагинация ─────────────────────────────────────────────────

    /// <summary>NestedSet, PageSize задан, Cursor=null (первая порция): TOP, без @cursor.</summary>
    [Fact]
    public void BuildNestedSetSql_Paging_FirstPage_HasTopWithoutCursor()
    {
        var src = CreateNestedSetSource(withLevelColumn: false) with { PageSize = 5 };
        var sql = ClayTreeSqlBuilder.BuildLevelSql(src, isRoot: false);

        Assert.Contains("TOP (@pageSize + 1)", sql);
        Assert.Contains("ORDER BY [L]", sql);
        Assert.DoesNotContain("@cursor", sql);
    }

    /// <summary>NestedSet, PageSize и Cursor заданы: TOP + @cursor.</summary>
    [Fact]
    public void BuildNestedSetSql_Paging_WithCursor_HasTopAndCursor()
    {
        var src = CreateNestedSetSource(withLevelColumn: false) with { PageSize = 5, Cursor = 10 };
        var sql = ClayTreeSqlBuilder.BuildLevelSql(src, isRoot: false);

        Assert.Contains("TOP (@pageSize + 1)", sql);
        Assert.Contains("[L] > @cursor", sql);
        Assert.Contains("ORDER BY [L]", sql);
    }

    /// <summary>NestedSet без пагинации: нет TOP, нет @cursor.</summary>
    [Fact]
    public void BuildNestedSetSql_NoPaging_NoTopNoCursor()
    {
        var src = CreateNestedSetSource(withLevelColumn: false); // PageSize=null
        var sql = ClayTreeSqlBuilder.BuildLevelSql(src, isRoot: false);

        Assert.DoesNotContain("TOP (", sql);
        Assert.DoesNotContain("@cursor", sql);
    }

    /// <summary>ParentKey с PageSize: игнор, без TOP/@cursor.</summary>
    [Fact]
    public void BuildParentKeySql_WithPageSize_IgnoresPaging()
    {
        var schema = new ClayTreeSchema
        {
            IdColumn = "Id", TextColumn = "Name", ParentColumn = "Parent"
        };
        var src = new ClayTreeSource("SELECT Id, Name, Parent FROM Tree",
            ClayTreeHierarchyMode.ParentKey, schema, PageSize: 5);
        var sql = ClayTreeSqlBuilder.BuildLevelSql(src, isRoot: false);

        Assert.DoesNotContain("TOP (", sql);
        Assert.DoesNotContain("@cursor", sql);
    }

    /// <summary>Корневой уровень + PageSize: без TOP/@cursor (корни не пагинируются).</summary>
    [Fact]
    public void BuildNestedSetSql_Root_WithPageSize_NoPaging()
    {
        var src = CreateNestedSetSource(withLevelColumn: false) with { PageSize = 5 };
        var sql = ClayTreeSqlBuilder.BuildLevelSql(src, isRoot: true);

        Assert.DoesNotContain("TOP (", sql);
        Assert.DoesNotContain("@cursor", sql);
    }

    // ── CTP: FromPagedRows ────────────────────────────────────────────────────

    /// <summary>Пришло n+1 строк → HasMore=true, вернулось n, NextCursor = L n-й.</summary>
    [Fact]
    public void FromPagedRows_MoreThanPageSize_HasMoreAndCursor()
    {
        var nodes = new List<ClayTreeNode>
        {
            new() { Id = "a", Left = 1 },
            new() { Id = "b", Left = 2 },
            new() { Id = "c", Left = 3 },
            new() { Id = "d", Left = 4 },
        }; // 4 nodes, pageSize=3 → запрошено TOP(4), лишняя отбрасывается

        var result = ClayTreeLoadResult.FromPagedRows(nodes, pageSize: 3);

        Assert.True(result.HasMore);
        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(1L, result.Nodes[0].Left);
        Assert.Equal(3L, result.Nodes[2].Left);
        Assert.Equal(3L, result.NextCursor);
    }

    /// <summary>Пришло ≤ n строк → HasMore=false.</summary>
    [Fact]
    public void FromPagedRows_NotMoreThanPageSize_NoMore()
    {
        var nodes = new List<ClayTreeNode>
        {
            new() { Id = "a", Left = 1 },
            new() { Id = "b", Left = 2 },
        }; // 2 nodes, pageSize=3

        var result = ClayTreeLoadResult.FromPagedRows(nodes, pageSize: 3);

        Assert.False(result.HasMore);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Equal(2L, result.NextCursor);
    }
}
