using System.Reflection;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Повторно открытый UPS принимает сохранённую исходную версию, а новые строки БД не входят в удаление.</summary>
    [Fact]
    public async Task UpsDraftReopen_PreservesOriginalRowsForCompareAndSwap()
    {
        const int upsId = 42;
        var testedAt = new DateTime(2026, 11, 21, 9, 14, 23, 654, DateTimeKind.Utc);
        var first = CreateFixture();
        string saved;
        await using (first.Context)
        {
            first.Service.StoredUpsServers.Add(new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 7 });
            first.Service.StoredUpsTests.Add(new UpsTest { Id = 1, UpsEquipmentId = upsId, TestedAt = testedAt, BatteryTime = 10 });
            var provider = first.Context.Render<MudDialogProvider>();
            var page = first.Context.Render<EquipmentForm>();
            WaitForSn(page, "CLAY_TEST_7");
            SetPrivate(page.Instance, "_canManageUps", true);
            var opening = TrackNextDialog(first.Context);
            var dialogTask = InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = upsId });
            var reference = await opening.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var dialog = provider.FindComponent<EquipmentUpsDialog>();
            await dialog.InvokeAsync(() => dialog.Instance.Tests[0].BatteryTime = 11);
            saved = (string)typeof(EquipmentForm).GetMethod("SerializeDraftState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page.Instance, null)!;
            using var savedJson = System.Text.Json.JsonDocument.Parse(saved);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, savedJson.RootElement.GetProperty("ActiveEditorState").ValueKind);
            await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
            await dialogTask;
        }

        var second = CreateFixture();
        await using var context = second.Context;
        second.Service.StoredUpsServers.AddRange([
            new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 7 },
            new UpsServerLink { UpsEquipmentId = upsId, ServerEquipmentId = 8 }
        ]);
        second.Service.StoredUpsTests.Add(new UpsTest { Id = 1, UpsEquipmentId = upsId, TestedAt = testedAt, BatteryTime = 20 });
        var secondProvider = context.Render<MudDialogProvider>();
        var reopened = context.Render<EquipmentForm>();
        WaitForSn(reopened, "CLAY_TEST_7");
        SetPrivate(reopened.Instance, "_canManageUps", true);
        await InvokeHandler(reopened, "RestoreDraftAsync", saved);

        var onOpened = TrackNextDialog(context);
        var reopenTask = InvokeHandler(reopened, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = upsId });
        await onOpened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var restored = secondProvider.FindComponent<EquipmentUpsDialog>();
        await restored.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        await reopenTask.WaitAsync(TimeSpan.FromSeconds(3));

        var originalTest = Assert.Single(second.Service.LastUpsOriginalTests!);
        Assert.Equal(10, originalTest.BatteryTime);
        Assert.Equal(testedAt, originalTest.TestedAt);
        Assert.Equal(11, Assert.Single(second.Service.LastUpsCurrentTests!).BatteryTime);
        Assert.Equal(7, Assert.Single(second.Service.LastUpsOriginalServers!).ServerEquipmentId);
    }

    /// <summary>Старый черновик UPS без исходных строк доступен для просмотра, запись из него запрещена.</summary>
    [Fact]
    public async Task UpsDraftLegacyWithoutOriginalDoesNotDispatch()
    {
        const int upsId = 42;
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canManageUps", true);
        var date = new DateTime(2026, 11, 21, 12, 14, 23);
        var saved = System.Text.Json.JsonSerializer.Serialize(new
        {
            Model = OriginalEquipment(page),
            OriginalSnapshot = (string)GetPrivate(page.Instance, "_originalSnapshot")!,
            ActiveEditor = new { Kind = nameof(EquipmentUpsDialog), Key = upsId.ToString() },
            ActiveUpsServers = Array.Empty<UpsServerLink>(),
            ActiveUpsTests = new[] { new UpsTest { Id = 0, UpsEquipmentId = upsId, TestedAt = date, BatteryTime = 10 } },
            ActiveEditorState = System.Text.Json.JsonSerializer.Serialize(new
            {
                UpsEquipmentId = upsId,
                Tests = new[] { new UpsTest { Id = 0, UpsEquipmentId = upsId, TestedAt = date, BatteryTime = 11 } },
            }),
        });
        await InvokeHandler(page, "RestoreDraftAsync", saved);
        var opening = TrackNextDialog(context);
        var dialogTask = InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = upsId });
        var reference = await opening.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dialog = provider.FindComponent<EquipmentUpsDialog>();
        var retained = Assert.Single(dialog.Instance.Tests);
        var okDisabled = dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled");

        var result = await dialog.InvokeAsync(() => dialog.Instance.Save!(
            new EquipmentUpsDialogResult(dialog.Instance.Servers, dialog.Instance.Tests)));

        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await dialogTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(11, retained.BatteryTime);
        Assert.True(okDisabled);
        Assert.False(result.CanClose);
        Assert.Contains("исходные строки UPS", result.Message);
        Assert.Null(fixture.Service.LastUpsCurrentTests);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }
}
