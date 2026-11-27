using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Сброс выбора ИБП учитывает точную ревизию и не создаёт связь.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpsLinkResetUsesExactRevisionAndNeverCreatesLink(bool acceptsRevision)
    {
        const int upsId = 42;
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(acceptsRevision);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canManageUps", true);
        var opened = TrackNextDialog(context);
        var opening = InvokeHandler(page, "OpenUpsLinkDialogAsync");
        var reference = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<EquipmentUpsLinkDialog>()));
        var editor = dialogs.FindComponent<EquipmentUpsLinkDialog>();
        Assert.NotNull(editor.Instance.EditorContext);
        await editor.InvokeAsync(() => editor.Instance.Value = upsId);

        var cancel = editor.InvokeAsync(() =>
            editor.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и начать заново").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));

        var write = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"]);
        Assert.Equal("child:EquipmentUpsLinkDialog:new:root", write.Arguments[1]);
        Assert.Equal(21L, write.Arguments[2]);
        Assert.Equal(false, write.Arguments[4]);
        using var parent = JsonDocument.Parse((string)write.Arguments[3]!);
        using var replacement = JsonDocument.Parse(parent.RootElement.GetProperty("ActiveEditorState").GetString()!);
        Assert.Equal(JsonValueKind.Null, replacement.RootElement.GetProperty("Value").ValueKind);
        Assert.Equal(acceptsRevision ? (int?)null : upsId, editor.Instance.Value);
        Assert.False(opening.IsCompleted);
        var access = Assert.Single(fixture.Service.UpsLinkResetReads);
        Assert.Equal(7, access.ServerId);
        Assert.True(access.EmployeeId > 0);
        Assert.Equal(0, fixture.Service.UpsWriteCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);

        await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Service.UpsWriteCalls);
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal List<(int ServerId, int EmployeeId)> UpsLinkResetReads { get; } = [];

        public override Task<bool> CanResetUpsLinkEditorAsync(int serverId, int employeeId, CancellationToken ct)
        {
            UpsLinkResetReads.Add((serverId, employeeId));
            return Task.FromResult(true);
        }
    }
}
