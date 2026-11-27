using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private const string RegisterEquipmentRam = "clayDraftStore.registerRam";



    [Fact]
    public async Task RamRegistration_KeyFailureDoesNotWrite()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestore?>(RegisterEquipmentRam, _ => true)
            .SetException(new Microsoft.JSInterop.JSException("key refused"));
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Не удалось открыть защищённый черновик", page.Markup));

        Assert.Single(context.JSInterop.Invocations[RegisterEquipmentRam]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Fact]
    public async Task RamRegistration_DoesNotListOrAdoptAClosedEditor()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations[RegisterEquipmentRam]));

        await InvokeHandler(page, "RefreshAsync");

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.listRecoveryCandidates"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recoverListedDraft"]);
        Assert.DoesNotContain("Сохранённый ввод", page.Markup);
        Assert.DoesNotContain("Открыть черновик", page.Markup);
        Assert.DoesNotContain("Проверить сохранённые черновики", page.Markup);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }
}
