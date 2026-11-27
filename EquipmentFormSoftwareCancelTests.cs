using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Отмена вложенного редактора защищает ввод и отделяет сброс от бизнес-записи.</summary>
    [Theory]
    [InlineData("continue")]
    [InlineData("close")]
    [InlineData("reset")]
    [InlineData("missing")]
    [InlineData("stale")]
    public async Task SoftwareCancelUsesExplicitDecisionAndExactScope(string scenario)
    {
        var fixture = CreateFixture(computer: true);
        await using var context = fixture.Context;
        context.Services.AddSingleton(new SoftwareEquipmentSelectDataSource(CreateDb(Script.Rows(), Script.Rows())));
        var original = new EquipmentSoftwareInstallation { EquipmentId = 7, Id = 17, Notes = "исходный текст" };
        fixture.Service.StoredSoftware.Add(original);
        if (scenario != "missing")
            fixture.Service.SoftwareResetResult = new() { EquipmentId = 7, Id = 17, Notes = "текст из БД" };
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(scenario != "stale");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var opened = TrackNextDialog(context);
        var opening = InvokeHandler(page, "OpenSoftwareDialogAsync", original);
        var reference = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<EquipmentSoftwareDialog>()));
        var editor = dialogs.FindComponent<EquipmentSoftwareDialog>();
        Assert.NotNull(editor.Instance.EditorContext);
        await editor.InvokeAsync(() => editor.Find("textarea").InputAsync("мой ввод"));

        var cancel = editor.InvokeAsync(() => editor.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        var text = scenario switch
        {
            "continue" => "Продолжить редактирование",
            "close" => "Закрыть без сохранения",
            _ => "Отменить мои изменения и загрузить из БД",
        };
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == text).ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));

        var writes = context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"];
        if (scenario is "reset" or "close" or "stale")
        {
            var write = Assert.Single(writes);
            Assert.Equal("child:EquipmentSoftwareDialog:17:root", write.Arguments[1]);
            Assert.Equal(21L, write.Arguments[2]);
            Assert.Equal(scenario == "close", write.Arguments[4]);
            using var parent = JsonDocument.Parse((string)write.Arguments[3]!);
            Assert.Equal(7, parent.RootElement.GetProperty("Model").GetProperty("Id").GetInt32());
            if (scenario == "close")
                Assert.Equal(JsonValueKind.Null, parent.RootElement.GetProperty("ActiveEditor").ValueKind);
            else
                Assert.Contains("текст из БД", JsonDocument.Parse(parent.RootElement.GetProperty("ActiveEditorState").GetString()!)
                    .RootElement.GetProperty("Value").GetProperty("Notes").GetString());
        }
        else
            Assert.Empty(writes);

        if (scenario == "close")
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
        else
        {
            Assert.False(opening.IsCompleted);
            Assert.Equal(scenario == "reset" ? "текст из БД" : "мой ввод", editor.Instance.Value.Notes);
            await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
        }
        Assert.Equal(scenario is "close" or "continue" ? 0 : 1, fixture.Service.SoftwareResetReads);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
    }
}
