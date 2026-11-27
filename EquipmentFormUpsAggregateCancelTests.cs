using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Сброс заменяет исходные строки UPS одной ревизией для следующего сохранения.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpsAggregateResetPublishesOneEnvelopeAndNextSaveUsesFreshBaseline(bool acceptsRevision)
    {
        const int upsId = 42;
        var oldTime = new DateTime(2026, 11, 21, 9, 14, 23, DateTimeKind.Utc);
        var freshTime = oldTime.AddMinutes(1);
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        fixture.Service.StoredUpsServers.Add(new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 7 });
        fixture.Service.StoredUpsTests.Add(new UpsTest { Id = 1, UpsEquipmentId = upsId, TestedAt = oldTime, BatteryTime = 10 });
        fixture.Service.UpsResetSnapshot = new EquipmentFormService.UpsEditorReset(
            [new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 7 },
             new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 8 }],
            [new UpsTest { Id = 1, UpsEquipmentId = upsId, TestedAt = freshTime, BatteryTime = 20 }]);
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(acceptsRevision);

        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canManageUps", true);
        var opened = TrackNextDialog(context);
        var opening = InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 7 });
        var reference = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var editor = dialogs.FindComponent<EquipmentUpsDialog>();
        await editor.InvokeAsync(() => editor.Instance.Tests[0].BatteryTime = 11);

        var cancel = editor.InvokeAsync(() =>
            editor.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));

        var cas = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"]);
        Assert.Equal("child:EquipmentUpsDialog:42:root", cas.Arguments[1]);
        Assert.Equal(21L, cas.Arguments[2]);
        Assert.Equal(false, cas.Arguments[4]);
        using var parent = JsonDocument.Parse((string)cas.Arguments[3]!);
        var payload = parent.RootElement;
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("ActiveEditorState").ValueKind);
        Assert.Equal(2, payload.GetProperty("ActiveUpsServers").GetArrayLength());
        Assert.Equal(20, payload.GetProperty("ActiveUpsTests")[0].GetProperty("BatteryTime").GetInt32());
        var baseline = payload.GetProperty("ActiveUpsOriginal");
        Assert.Equal(upsId, baseline.GetProperty("UpsEquipmentId").GetInt32());
        Assert.Equal(2, baseline.GetProperty("Servers").GetArrayLength());
        Assert.Equal(freshTime, baseline.GetProperty("Tests")[0].GetProperty("TestedAt").GetDateTime());
        Assert.Equal(20, baseline.GetProperty("Tests")[0].GetProperty("BatteryTime").GetInt32());
        var read = Assert.Single(fixture.Service.UpsResetReads);
        Assert.Equal((upsId, 7), (read.UpsId, read.ServerId));
        Assert.True(read.EmployeeId > 0);
        Assert.Equal(0, fixture.Service.UpsWriteCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.False(opening.IsCompleted);

        Assert.Equal(acceptsRevision ? 20 : 11, Assert.Single(editor.Instance.Tests).BatteryTime);
        Assert.Equal(acceptsRevision ? 2 : 1, editor.Instance.Servers.Count);
        if (acceptsRevision)
        {
            await editor.InvokeAsync(() => editor.Instance.Tests[0].BatteryTime = 21);
            await editor.InvokeAsync(() =>
                editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync());
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
            var accepted = Assert.Single(fixture.Service.LastUpsOriginalTests!);
            Assert.Equal(freshTime, accepted.TestedAt);
            Assert.Equal(20, accepted.BatteryTime);
            Assert.Equal(new[] { 7, 8 }, fixture.Service.LastUpsOriginalServers!.Select(server => server.ServerEquipmentId));
            Assert.Equal(21, Assert.Single(fixture.Service.LastUpsCurrentTests!).BatteryTime);
            Assert.Equal(1, fixture.Service.UpsWriteCalls);
        }
        else
        {
            await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(0, fixture.Service.UpsWriteCalls);
        }
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal EquipmentFormService.UpsEditorReset? UpsResetSnapshot { get; set; }
        internal List<(int UpsId, int ServerId, int EmployeeId)> UpsResetReads { get; } = [];

        public override Task<EquipmentFormService.UpsEditorReset?> ReadUpsEditorResetAsync(int upsId, int serverId,
            int employeeId, CancellationToken ct)
        {
            UpsResetReads.Add((upsId, serverId, employeeId));
            return Task.FromResult(UpsResetSnapshot);
        }
    }
}
