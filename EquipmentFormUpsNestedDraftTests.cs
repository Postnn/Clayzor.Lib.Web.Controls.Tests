using System.Reflection;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private const string UpsNestedScope = "child:EquipmentUpsDialog:42:1";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task UpsNestedTest_DraftRestoresBeforeOkAndClosesOnlyOwnScope(bool unknown, bool pending, bool accept)
    {
        var first = CreateFixture();
        string saved;
        await using (first.Context)
        {
            ConfigureUpsDraftFixture(first, false);
            var provider = first.Context.Render<MudDialogProvider>();
            var page = first.Context.Render<EquipmentForm>();
            WaitForSn(page, "CLAY_TEST_7");
            SetPrivate(page.Instance, "_canManageUps", true);
            var outerAdded = TrackNextDialog(first.Context);
            var opening = InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = 42 });
            var outerReference = await outerAdded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var outer = provider.FindComponent<EquipmentUpsDialog>();
            var innerAdded = TrackNextDialog(first.Context);
            var edit = typeof(EquipmentUpsDialog).GetMethod("EditTest", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var innerTask = outer.InvokeAsync(() => (Task)edit.Invoke(outer.Instance, [outer.Instance.Tests[0]])!);
            var innerReference = await innerAdded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var inner = provider.FindComponent<EquipmentUpsTestDialog>();
            inner.WaitForAssertion(() => Assert.StartsWith("equipment-draft-dialog-",
                inner.Find($"[data-draft-scope='{UpsNestedScope}']").GetAttribute("class")));
            await inner.Find("textarea[data-draft-key='ups-test.issues']").InputAsync("Незавершённый текст");
            inner.WaitForAssertion(() =>
            {
                using var document = JsonDocument.Parse(SerializeEquipmentDraft(page));
                var draft = document.RootElement.GetProperty("ActiveUpsTest");
                Assert.Equal("Незавершённый текст", draft.GetProperty("Issues").GetString());
                Assert.Equal(10, draft.GetProperty("Original").GetProperty("BatteryTime").GetInt32());
            });
            saved = SerializeEquipmentDraft(page);
            Assert.Empty(first.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
            await provider.InvokeAsync(() => innerReference.Close(DialogResult.Cancel()));
            await innerTask.WaitAsync(TimeSpan.FromSeconds(3));
            await provider.InvokeAsync(() => outerReference.Close(DialogResult.Cancel()));
            await opening.WaitAsync(TimeSpan.FromSeconds(3));
        }

        var second = CreateFixture();
        await using var context = second.Context;
        ConfigureUpsDraftFixture(second, unknown, pending);
        var secondProvider = context.Render<MudDialogProvider>();
        var reopened = context.Render<EquipmentForm>();
        WaitForSn(reopened, "CLAY_TEST_7");
        SetPrivate(reopened.Instance, "_canManageUps", true);
        await InvokeHandler(reopened, "RestoreDraftAsync", saved);
        var reopening = InvokeHandler(reopened, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = 42 });
        secondProvider.WaitForAssertion(() => Assert.Single(secondProvider.FindComponents<EquipmentUpsTestDialog>()));
        var restored = secondProvider.FindComponent<EquipmentUpsTestDialog>();
        Assert.Equal("Незавершённый текст", restored.Instance.Value.Issues);
        restored.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations["clayDraftStore.applyRaw"],
            call => call.Arguments.Count > 1 && Equals(call.Arguments[1], UpsNestedScope)));

        if (pending)
        {
            restored.WaitForAssertion(() => Assert.Contains("Восстановление ввода теста UPS не завершено", restored.Markup));
            Assert.True(restored.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        }
        var closeButton = accept ? "OK" : "Отмена";
        await restored.FindAll("button").Single(button => button.TextContent.Trim() == closeButton).ClickAsync();

        if (unknown || pending)
        {
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal(1, ((EquipmentUpsTestDraft)GetPrivate(reopened.Instance, "_upsTestDraft")!).Original.Id);
        }
        else
        {
            var cleanup = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal(UpsNestedScope, cleanup.Arguments[1]);
            Assert.Null(GetPrivate(reopened.Instance, "_upsTestDraft"));
        }
        var parent = secondProvider.FindComponent<EquipmentUpsDialog>();
        Assert.Equal(accept ? "Незавершённый текст" : string.Empty, parent.Instance.Tests[0].Issues);
        Assert.Null(second.Service.LastUpsCurrentTests);
        var parentDialog = (IMudDialogInstance)typeof(EquipmentEditorDialogBase<EquipmentUpsDialogResult>)
            .GetProperty("Dialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parent.Instance)!;
        await secondProvider.InvokeAsync(() => parentDialog.Close(DialogResult.Cancel()));
        await reopening.WaitAsync(TimeSpan.FromSeconds(3));
        if (unknown || pending)
        {
            Assert.NotNull(GetPrivate(reopened.Instance, "_upsTestDraft"));
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        }
    }

    private static string SerializeEquipmentDraft(IRenderedComponent<EquipmentForm> page) =>
        (string)typeof(EquipmentForm).GetMethod("SerializeDraftState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page.Instance, null)!;

    private static void ConfigureUpsDraftFixture(Fixture fixture, bool unknown, bool pending = false)
    {
        fixture.Service.StoredUpsServers.Add(new UpsServerLink { UpsEquipmentId = 42, ServerEquipmentId = 7 });
        fixture.Service.StoredUpsTests.Add(new UpsTest
        {
            Id = 1, UpsEquipmentId = 42, TestedAt = new DateTime(2026, 11, 21, 9, 14, 23, DateTimeKind.Utc),
            BatteryTime = 10,
        });
        fixture.Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], pending ? ["data:ups-test.time:0"] : []));
        fixture.Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], pending ? ["data:ups-test.time:0"] : []));
        fixture.Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(11, true, unknown));
        fixture.Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
    }
}
