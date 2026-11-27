using Bunit;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    [Fact]
    public async Task EquipmentPresenceUsesTheConfirmedExistingEntityKey()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.Equal(ClayPresenceKey.Create("inventory", "equipment", 7),
            page.FindComponent<ClayPresencePanel>().Instance.Target?.Key));
    }

    [Fact]
    public async Task MainFieldChangeMarksOnlyEditedSerialAndClearsAfterRevert()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        string marker = "data-clay-field-changed";
        Assert.False(FindSn(page).Closest(".clay-field-change")!.HasAttribute(marker));

        await page.InvokeAsync(() => FindSn(page).InputAsync("CLAY_TEST_LOCAL"));
        page.WaitForAssertion(() =>
        {
            var changed = Assert.Single(page.FindAll($"[{marker}]"));
            Assert.NotNull(changed.QuerySelector("input[data-draft-key='main.sn']"));
        });

        await page.InvokeAsync(() => FindSn(page).InputAsync("CLAY_TEST_7"));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll($"[{marker}]")));
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EquipmentFocus_FirstEditorIsSerialNumberAndRequiresWriteAccess(bool canEdit)
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        if (!canEdit)
            fixture.Service.Roles.Remove((int)Роли.Специалист_по_инвентаризации_оборудования);
        const string modulePath = "./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js";
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.JSInterop.SetupModule(modulePath).SetupVoid("update", call =>
        {
            if (Equals(call.Arguments[1], canEdit)) updated.TrySetResult();
            return true;
        }).SetVoidResult();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        page.WaitForAssertion(() =>
        {
            var scope = page.FindComponent<ClayEditorFocusScope>();
            Assert.Equal(canEdit, scope.Instance.Ready);
            Assert.True(scope.Instance.AdvanceOnEnter);
            Assert.Equal("main.sn", scope.FindAll("input").First().GetAttribute("data-draft-key"));
        });
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
