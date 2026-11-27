using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Сброс SIM открывает свежую версию и принимает PIN только в памяти.</summary>
    [Theory]
    [InlineData("accepted")]
    [InlineData("unavailable")]
    [InlineData("stale")]
    public async Task SimResetKeepsSecretsLocalAndRequiresCurrentRevision(string outcome)
    {
        var fixture = CreateFixture(sim: true);
        await using var context = fixture.Context;
        context.Services.AddSingleton(new MobileContractSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new PersonalDataPersonSelectDataSource(CreateDb()));
        var original = new EquipmentSimCard
        {
            EquipmentId = 7, PhoneNumber = "79000000001", ActivatedAtUtc = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc),
            EditorVersion = "v1", Secrets = new EquipmentSimSecrets { Pin = "1234" },
        };
        fixture.Service.SimEditorSnapshot = original;
        if (outcome != "unavailable")
            fixture.Service.SimResetSnapshot = new EquipmentSimCard
            {
                EquipmentId = 7, PhoneNumber = original.PhoneNumber, ActivatedAtUtc = original.ActivatedAtUtc.AddHours(1),
                EditorVersion = "v2", Secrets = new EquipmentSimSecrets { Pin = "9876" },
            };
        context.Services.AddSingleton<IJSRuntime>(new SensitiveRuntime(fixture.Js, new SensitiveModule(outcome == "stale")));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_simCards", new List<EquipmentSimCard> { original });
        var added = TrackNextDialog(context);
        var opening = InvokeHandler(page, "OpenSimDialogAsync");
        var reference = await added.Task.WaitAsync(TimeSpan.FromSeconds(3));
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<EquipmentSimDialog>()));
        var editor = dialogs.FindComponent<EquipmentSimDialog>();
        editor.WaitForAssertion(() => Assert.Equal("1234", editor.Instance.Secrets.Pin));
        await editor.InvokeAsync(() => editor.Find("input[type='password']").InputAsync("4567"));
        var cancel = editor.InvokeAsync(() => editor.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(outcome == "accepted" ? "9876" : "4567", editor.Instance.Secrets.Pin);
        Assert.Equal(outcome == "accepted" ? "v2" : "v1", editor.Instance.OriginalVersion);
        var replacements = context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"];
        Assert.Equal(outcome == "unavailable" ? 0 : 1, replacements.Count);
        foreach (var item in replacements.Concat(context.JSInterop.Invocations["clayDraftStore.saveTyped"]))
            foreach (var text in item.Arguments.OfType<string>())
            {
                Assert.DoesNotContain("\"Pin\"", text);
                Assert.DoesNotContain("\"Secrets\"", text);
            }
        await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(4));
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal EquipmentSimCard? SimResetSnapshot { get; set; }

        public override Task<(EquipmentSimCard Snapshot, int CustomerType)?> ReadSimEditorResetAsync(
            int equipmentId, bool expectedExisting, int employeeId, CancellationToken ct)
        {
            Assert.Equal((7, true, 4), (equipmentId, expectedExisting, employeeId));
            return Task.FromResult<(EquipmentSimCard Snapshot, int CustomerType)?>(SimResetSnapshot is { } snapshot
                ? (snapshot, 0) : null);
        }
    }
}
