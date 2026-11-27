using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Незавершённая main-область не принимается, посторонняя область не блокирует запись.</summary>
    [Theory]
    [InlineData("main", false, false)]
    [InlineData("main", true, false)]
    [InlineData("child:other:new:root", false, true)]
    public async Task SaveEquipment_RequiresReadinessOfAcceptedScope(string scope, bool failed, bool writes)
    {
        var readiness = new ClayDraftRestoreReadiness(false, failed ? [] : [scope], failed ? [scope] : []);
        var fixture = CreateFixture(draftReadiness: readiness);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_INPUT");

        await InvokeSave(page, false);

        Assert.Equal(writes ? 1 : 0, fixture.Service.UpdateCalls);
        if (!writes)
        {
            Assert.Contains("Сначала полностью восстановите ввод", page.Markup);
            Assert.Equal("CLAY_TEST_INPUT", FindSn(page).GetAttribute("value"));
        }
    }

    /// <summary>Незавершённое восстановление открытого child-редактора блокирует его запись.</summary>
    [Fact]
    public async Task SaveComputer_PendingScopeDoesNotDispatch()
    {
        var scope = $"child:{nameof(EquipmentComputerDialog)}:new:root";
        var readiness = new ClayDraftRestoreReadiness(false, [scope], []);
        var fixture = CreateFixture(computer: true, draftReadiness: readiness);
        fixture.Service.ComputerSaveCompletion.SetResult();
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var opening = InvokeHandler(page, "OpenComputerDialogAsync");
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dialog = provider.FindComponent<EquipmentComputerDialog>();

        dialog.WaitForAssertion(() => Assert.Contains("Не все поля восстановлены", dialog.Markup));
        Assert.True(dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fixture.Service.ComputerSaveCalls);
    }
}
