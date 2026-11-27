using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Сброс дочернего окна сохраняет ввод при конфликте серверной версии связи.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquipmentPhoneResetRequiresMatchingParentBaseline(bool changed)
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var row = new EquipmentPhoneLink { EquipmentId = 7, PhoneId = 17, Notes = "old" };
        fixture.Service.PhoneReset = new EquipmentPhoneLink { EquipmentId = 7, PhoneId = 17, Notes = changed ? "new server" : "old" };
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_phones", new List<EquipmentPhoneLink> { row });
        SetPrivate(page.Instance, "_originalPhones", new List<EquipmentPhoneLink> { row });
        var added = TrackNextDialog(context);
        var opening = InvokeHandler(page, "EditPhoneReturnAsync", row);
        var reference = await added.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var editor = dialogs.FindComponent<EquipmentPhoneDialog>();
        await editor.InvokeAsync(() => editor.Find("input[data-draft-key='phone.notes']").InputAsync("local"));
        Assert.Single(editor.FindAll("[data-clay-field-changed]"));
        var cancel = editor.InvokeAsync(() => editor.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(changed ? "local" : "old", editor.Instance.Notes);
        Assert.Equal(changed ? 1 : 0, editor.FindAll("[data-clay-field-changed]").Count);
        Assert.Equal(changed ? 0 : 1, context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"].Count);
        Assert.Equal("old", row.Notes);
        Assert.Equal(0, fixture.Service.SyncCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(4));
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal EquipmentPhoneLink? PhoneReset { get; set; }

        public override Task<(EquipmentPhoneLink? Link, bool CanReset)> ReadPhoneReturnEditorResetAsync(
            int equipmentId, int phoneId, int? connectionEquipmentId, bool expectedExisting, int employeeId, CancellationToken ct)
        {
            Assert.Equal((7, 17, true, 4), (equipmentId, phoneId, expectedExisting, employeeId));
            return Task.FromResult((PhoneReset, true));
        }
    }
}
