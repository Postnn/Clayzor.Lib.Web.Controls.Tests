using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки typed state дочерних редакторов EQ9.</summary>
public sealed class Eq9EditorStateTests
{
    /// <summary>Location restore возвращает выбранный ID, а не только отображаемый текст.</summary>
    [Fact]
    public void LocationState_RestoresSelectedId()
    {
        var source = new EquipmentLocationDialog { LocationId = 99, Notes = "draft" };
        var context = new EquipmentEditorContext(7, 1, () => true) { Component = source };
        var json = context.CaptureDraftState();
        var target = new EquipmentLocationDialog();

        context.Component = target;
        context.RestoreDraftState(json!);

        Assert.Equal(99, target.LocationId);
        Assert.Equal("draft", target.Notes);
    }

    /// <summary>Accounting baseline не заменяется свежей V2 при восстановлении V1 draft.</summary>
    [Fact]
    public void AccountingState_PreservesOriginalVersion()
    {
        var v1 = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var source = new EquipmentAccountingDialog
        {
            Value = new EquipmentAccounting { Id = 15, EquipmentId = 7, ChangedAt = v1, Notes = "local" },
        };
        var context = new EquipmentEditorContext(7, 1, () => true) { Component = source };
        var json = context.CaptureDraftState();
        var target = new EquipmentAccountingDialog
        {
            Value = new EquipmentAccounting { Id = 15, EquipmentId = 7, ChangedAt = v1.AddSeconds(1), Notes = "fresh" },
        };

        context.Component = target;
        context.RestoreDraftState(json!);

        Assert.Equal(v1, target.Value.ChangedAt);
        Assert.Equal("local", target.Value.Notes);
    }

    /// <summary>Computer restore сохраняет структуру добавленных и удалённых компонентов.</summary>
    [Fact]
    public void ComputerState_RestoresDynamicComponentStructure()
    {
        var source = new EquipmentComputerDialog();
        source.Ram.AddRange([1024, 2048]);
        source.Hd.AddRange([100.5m, 200.25m]);
        source.Ssd.AddRange([128, 256]);
        var context = new EquipmentEditorContext(7, 1, () => true) { Component = source };
        var json = context.CaptureDraftState();
        var target = new EquipmentComputerDialog();
        target.Ram.Add(4096);
        target.Hd.Add(999);

        context.Component = target;
        context.RestoreDraftState(json!);

        Assert.Equal([1024m, 2048m], target.Ram);
        Assert.Equal([100.5m, 200.25m], target.Hd);
        Assert.Equal([128m, 256m], target.Ssd);
    }

    /// <summary>Пароль не попадает в typed JSON, а выбранный UPS и логин восстанавливаются.</summary>
    [Fact]
    public void SecretsAndIds_KeepSecretOutOfTypedState()
    {
        var account = new EquipmentAccountDialog { Login = "account-a", Notes = "draft" };
        var context = new EquipmentEditorContext(7, 1, () => true) { Component = account };
        var json = context.CaptureDraftState();
        var ups = new EquipmentUpsLinkDialog();

        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("account-a", json);
        context.Component = ups;
        context.RestoreDraftState("""{"Value":15}""");
        Assert.Equal(15, ups.Value);
    }
}
