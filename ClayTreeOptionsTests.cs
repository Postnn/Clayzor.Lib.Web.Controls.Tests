using Clayzor.Lib.Web.Controls.Components.Tree;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты опций дерева <see cref="ClayTreeOptions"/>.
/// </summary>
public class ClayTreeOptionsTests
{
    /// <summary>Дефолт MaxFilterRecords — 100.</summary>
    [Fact]
    public void Defaults_MaxFilterRecords_Is100()
    {
        var opts = new ClayTreeOptions();
        Assert.Equal(100, opts.MaxFilterRecords);
    }

    /// <summary>Дефолт SelectionMode — Single.</summary>
    [Fact]
    public void Defaults_SelectionMode_IsSingle()
    {
        var opts = new ClayTreeOptions();
        Assert.Equal(ClayTreeSelectionMode.Single, opts.SelectionMode);
    }

    /// <summary>Дефолт FilterExcludedColumns — не-null, пустой.</summary>
    [Fact]
    public void Defaults_FilterExcludedColumns_NotNullAndEmpty()
    {
        var opts = new ClayTreeOptions();
        Assert.NotNull(opts.FilterExcludedColumns);
        Assert.Empty(opts.FilterExcludedColumns);
    }

    /// <summary>Дефолт FilterDefaults — не-null, пустой.</summary>
    [Fact]
    public void Defaults_FilterDefaults_NotNullAndEmpty()
    {
        var opts = new ClayTreeOptions();
        Assert.NotNull(opts.FilterDefaults);
        Assert.Empty(opts.FilterDefaults);
    }

    /// <summary>Дефолт FilterColumns — null (фильтр недоступен, пока страница не задаст колонки).</summary>
    [Fact]
    public void Defaults_FilterColumns_IsNull()
    {
        var opts = new ClayTreeOptions();
        Assert.Null(opts.FilterColumns);
    }
}
