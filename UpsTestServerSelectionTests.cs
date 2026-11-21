using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Rules;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки legacy-формата и правил выбора серверов UPS-теста.</summary>
public sealed class UpsTestServerSelectionTests
{
    /// <summary>Доступны только компьютеры с именем; исторические имена сохраняются read-only.</summary>
    [Fact]
    public void Rows_FilterAvailableAndPreserveHistoricalNames()
    {
        var selected = UpsTestServerSelection.ParseSelected("srv-b,legacy,srv-b", ["srv-a", "srv-b"]);
        var rows = UpsTestServerSelection.BuildRows(selected,
        [
            new UpsServerLink { ServerEquipmentId = 2, ServerName = "srv-b", EquipmentTypeId = 1 },
            new UpsServerLink { ServerEquipmentId = 1, ServerName = "srv-a", EquipmentTypeId = 1 },
            new UpsServerLink { ServerEquipmentId = 3, ServerName = "not-computer", EquipmentTypeId = 2 },
            new UpsServerLink { ServerEquipmentId = 4, ServerName = "", EquipmentTypeId = 1 },
        ]);

        Assert.Equal(new[] { "srv-a", "srv-b", "legacy" }, rows.Select(row => row.Name).ToArray());
        Assert.False(rows[0].IsChecked);
        Assert.True(rows[1].IsChecked);
        Assert.False(rows[2].IsRecognized);
        Assert.Equal("srv-b,legacy", UpsTestServerSelection.Format(selected));
    }

    /// <summary>Toggle меняет выбор, а Issues дописывает только неотмеченные серверы без повторов.</summary>
    [Fact]
    public void ToggleAndPatchIssues_UseOnlyUncheckedRecognizedServers()
    {
        var selected = UpsTestServerSelection.ParseSelected("srv-a", ["srv-a", "srv-b"]);
        var rows = UpsTestServerSelection.BuildRows(selected,
        [
            new UpsServerLink { ServerEquipmentId = 1, ServerName = "srv-a", EquipmentTypeId = 1 },
            new UpsServerLink { ServerEquipmentId = 2, ServerName = "srv-b", EquipmentTypeId = 1 },
        ]);
        UpsTestServerSelection.Toggle(selected, rows[1]);
        Assert.Equal("srv-a,srv-b", UpsTestServerSelection.Format(selected));
        UpsTestServerSelection.Toggle(selected, rows[0]);
        Assert.Equal("srv-b", UpsTestServerSelection.Format(selected));

        rows = UpsTestServerSelection.BuildRows(selected,
        [
            new UpsServerLink { ServerEquipmentId = 1, ServerName = "srv-a", EquipmentTypeId = 1 },
            new UpsServerLink { ServerEquipmentId = 2, ServerName = "srv-b", EquipmentTypeId = 1 },
        ]);
        var issues = UpsTestServerSelection.PatchIssues("existing", rows);
        Assert.Equal("existing. Сервера, не получившие уведомление: srv-a", issues);
        Assert.Equal(issues, UpsTestServerSelection.PatchIssues(issues, rows));
    }
}
