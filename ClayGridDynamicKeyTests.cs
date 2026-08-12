using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Unit-тесты value-based equality для <see cref="ClayGridDynamicKey"/> (CGFR1 §33).
/// </summary>
public class ClayGridDynamicKeyTests
{
    private static ClayGridDynamicSettings SampleSettings() => new()
    {
        ConnectionStringName = "DefaultConnection",
        SettingsTable = "ClayGridSettings",
        ColumnsTable = "ClayGridColumns",
        UserParamsTable = "ClayGridUserParams",
        UserSharedParamsTable = "ClayGridUserSharedParams",
        UserParamsShared = "ClayGridUserParamsShared",
        GridIdQueryParam = "id",
        ClientIdQueryParam = "CLID",
        ColumnsParamPrefix = "cols",
        FilterParamPrefix = "flt",
        GroupingParamPrefix = "grp",
        SortingParamPrefix = "srt",
        PageSizeParamPrefix = "pgs",
        QuickSearchParamPrefix = "qks",
    };

    [Fact]
    public void SameValues_Equal()
    {
        var s = SampleSettings();
        var a = ClayGridDynamicKey.Create(101, 1, null, s);
        var b = ClayGridDynamicKey.Create(101, 1, null, s);
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void GridIdChanged_NotEqual()
    {
        var s = SampleSettings();
        var a = ClayGridDynamicKey.Create(101, 1, null, s);
        var b = ClayGridDynamicKey.Create(202, 1, null, s);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ClidChanged_NotEqual()
    {
        var s = SampleSettings();
        var a = ClayGridDynamicKey.Create(101, 1, null, s);
        var b = ClayGridDynamicKey.Create(101, 2, null, s);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SharedIdNullToValue_NotEqual()
    {
        var s = SampleSettings();
        var a = ClayGridDynamicKey.Create(101, 1, null, s);
        var b = ClayGridDynamicKey.Create(101, 1, 5, s);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SharedIdValueToOtherValue_NotEqual()
    {
        var s = SampleSettings();
        var a = ClayGridDynamicKey.Create(101, 1, 3, s);
        var b = ClayGridDynamicKey.Create(101, 1, 7, s);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ConnectionStringNameChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.ConnectionStringName = "OtherDB";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void SettingsTableChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.SettingsTable = "OtherTable";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void ColumnsTableChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.ColumnsTable = "OtherColumnsTable";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void UserParamsTableChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.UserParamsTable = "OtherUP";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void UserSharedParamsTableChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.UserSharedParamsTable = "OtherUSP";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void UserParamsSharedChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.UserParamsShared = "OtherUPS";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void GridIdQueryParamChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.GridIdQueryParam = "gid";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void ClientIdQueryParamChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.ClientIdQueryParam = "CID";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void ParamPrefixChanged_NotEqual()
    {
        var s1 = SampleSettings();
        var s2 = SampleSettings(); s2.ColumnsParamPrefix = "c";
        Assert.NotEqual(
            ClayGridDynamicKey.Create(101, 1, null, s1),
            ClayGridDynamicKey.Create(101, 1, null, s2));
    }

    [Fact]
    public void SameIdentity_SameKey()
    {
        var s = SampleSettings();
        var k1 = ClayGridDynamicKey.Create(101, 1, 3, s);
        var k2 = ClayGridDynamicKey.Create(101, 1, 3, s);
        Assert.Equal(k1, k2);
    }
}
