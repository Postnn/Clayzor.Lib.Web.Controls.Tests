using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки диагностических правил DisplayNet.</summary>
public sealed class EquipmentNetworkDiagnosticsTests
{
    /// <summary>Количество портов сравнивается с Net, а два порта требуют MAC и MAC2.</summary>
    [Theory]
    [InlineData(2, 1, "001122334455", "66778899AABB", 1, true)]
    [InlineData(2, 2, "001122334455", null, 1, true)]
    [InlineData(2, 2, null, "66778899AABB", 1, true)]
    [InlineData(2, 2, "001122334455", "66778899AABB", 1, false)]
    [InlineData(3, 3, "001122334455", "66778899AABB", 1, false)]
    [InlineData(1, 2, null, null, 1, false)]
    public void Warning_UsesModelNetAndBothMacs(
        int ports, int modelNet, string? mac, string? mac2, int typeId, bool expected) =>
        Assert.Equal(expected, EquipmentNetworkDiagnostics.HasWarning(ports, modelNet, mac, mac2, typeId));

    /// <summary>Switch/router при количестве меньше трёх исключаются из warning.</summary>
    [Theory]
    [InlineData(33)]
    [InlineData(35)]
    public void Warning_SwitchAndRouterWithLessThanThreePorts_AreExcluded(int typeId) =>
        Assert.False(EquipmentNetworkDiagnostics.HasWarning(2, 0, null, null, typeId));

    /// <summary>Switch/router с тремя и более найденными портами скрывают секцию.</summary>
    [Theory]
    [InlineData(2, 33, false)]
    [InlineData(3, 33, true)]
    [InlineData(4, 35, true)]
    [InlineData(4, 1, false)]
    public void HideSection_UsesSwitchRouterException(int ports, int typeId, bool expected) =>
        Assert.Equal(expected, EquipmentNetworkDiagnostics.HideSection(ports, typeId));
}
