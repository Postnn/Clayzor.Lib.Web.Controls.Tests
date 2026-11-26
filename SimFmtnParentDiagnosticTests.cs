using System.Reflection;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.AspNetCore.WebUtilities;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Фикстура существующего завершения после подтверждённой записи, без выполнения записи.</summary>
public sealed class SimFmtnParentDiagnosticTests
{
    /// <summary>Обычное завершение обновляет opener, но legacy-контекст теряет адресное уведомление.</summary>
    [Theory]
    [InlineData("save", false)]
    [InlineData("save", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    public async Task CompletionShowsTheMissingLegacyTargetTransport(string outcome, bool parentContext)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var navigation = new FakeNavigationManager();
        const string existing = "00123456789";
        const string replacement = "00123456790";
        var parent = parentContext ? "&parentidp=page-proof-absent&parentId=7&parentName=inventory" : "";
        navigation.NavigateTo($"http://localhost/Equipment/SIM_FMTN?id={existing}{parent}");
        var page = new SimFmtnForm { Id = existing };
        try
        {
            if (parentContext)
            {
                page.ParentPage = "page-proof-absent";
                page.ParentId = "7";
                page.ParentName = "inventory";
            }
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(SimFmtnForm);
            type.GetProperty("JS", flags)!.SetValue(page, context.JSInterop.JSRuntime);
            type.GetProperty("Navigation", flags)!.SetValue(page, navigation);
            if (outcome != "delete")
                type.GetField("_original", flags)!.SetValue(page,
                    new SimFmtn { Prefix = outcome == "rename" ? replacement : existing });

            var method = outcome == "delete" ? "CompleteAsync" : "CompleteSaveAsync";
            await (Task)type.GetMethod(method, flags)!.Invoke(page, null)!;

            Assert.Equal(parentContext ? 0 : 1, context.JSInterop.Invocations["clayModelEquipmentForm.refreshOpener"].Count);
            Assert.Equal(outcome == "rename" ? 0 : 1, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
            if (outcome == "rename")
            {
                var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
                Assert.Equal(replacement, query["id"].ToString());
                if (parentContext) Assert.Equal("page-proof-absent", query["parentidp"].ToString());
            }
        }
        finally { await page.DisposeAsync(); }
    }
}
