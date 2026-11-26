using Bunit;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using static Clayzor.Lib.Web.Controls.Tests.ModelEquipmentWriteBoundaryTests;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class ModelEquipmentFormTests
{
    private const string ModelFocusModulePath = "./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js";

    [Theory]
    [InlineData("ready")]
    [InlineData("readonly")]
    [InlineData("pending")]
    [InlineData("unknown")]
    public async Task ModelFocusReadyRequiresEditorRightsAndResolvedInput(string state)
    {
        const string url = "http://localhost/Equipment/ModelEquipmentForm?id=7";
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = (int)ТипыОборудования.IP_телефон, Name = "Телефон" };
        var fixture = CreateFixture(url, [Script.Rows(TypeRow(model.EquipmentTypeId)), Script.Rows()], model, usageCount: 4);
        if (state == "readonly")
        {
            fixture.Commands.Clear();
            fixture.Enqueue(ModelRow(model), Roles(Роли.Дежурный_инженер), Usage(), Script.Rows(TypeRow(model.EquipmentTypeId)));
        }
        await using var context = fixture.Context;
        if (state == "unknown")
            SetupRestore(context, "unknown");
        else if (state == "pending")
        {
            context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
                .SetResult(new ClayDraftRestore("model-fixture", true, 10, null, null));
            context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
                .SetResult(new ClayDraftEditorScopeState(10, true, false));
            context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], ["data:model.name:0"]));
        }

        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.JSInterop.SetupModule(ModelFocusModulePath).SetupVoid("update", invocation =>
        {
            if (Equals(invocation.Arguments[1], state == "ready"))
                updated.TrySetResult();
            return true;
        }).SetVoidResult();
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() =>
        {
            var scope = page.FindComponent<ClayEditorFocusScope>();
            Assert.Equal(state == "ready", scope.Instance.Ready);
            Assert.True(scope.Instance.AdvanceOnEnter);
            Assert.Empty(scope.FindAll("a[aria-label='Открыть список оборудования данной модели']"));
            Assert.DoesNotContain("Шаблон телефона:", scope.Markup);
        });
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (state == "ready")
        {
            Assert.NotEmpty(page.FindAll("a[aria-label='Открыть список оборудования данной модели']"));
            Assert.Contains("Шаблон телефона:", page.Markup);
        }
        Assert.Equal(0, fixture.WriteCount);
    }

    [Fact]
    public async Task AcceptedModelTypeChoiceRequestsAdvanceAfterBinding()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5",
            [Script.Rows(TypeRow(5)), Script.Rows(TypeRow(6)), Script.Rows(TypeRow(6))]);
        await using var context = fixture.Context;
        context.JSInterop.SetupModule(ModelFocusModulePath).Setup<long?>("captureSelectionAdvance", _ => true).SetResult(21);
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.True(page.FindComponent<ClayEditorFocusScope>().Instance.Ready));
        var type = page.FindComponent<ClaySelect<EquipmentType, int?>>();
        Assert.True(type.Instance.Options!.AdvanceFocusOnSelection);
        Assert.True(page.FindComponent<ClaySelect<ResourceLookup, int?>>().Instance.Options!.AdvanceFocusOnSelection);
        await type.Find("button.clay-select-button").ClickAsync();
        await type.Find("[role='option']").ClickAsync();

        Assert.Equal(6, CurrentModel(page).EquipmentTypeId);
        Assert.Equal(21L, Assert.Single(context.JSInterop.Invocations["completeSelectionAdvance"]).Arguments[1]);
        Assert.Equal(0, fixture.WriteCount);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    [Fact]
    public async Task ModelRouteChangeStartsASeparateFocusScope()
    {
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Первая модель" };
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows(), Script.Rows(TypeRow(5)), Script.Rows()], model);
        await using var context = fixture.Context;
        context.JSInterop.SetupModule(ModelFocusModulePath);
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.True(page.FindComponent<ClayEditorFocusScope>().Instance.Ready));
        var first = page.FindComponent<ClayEditorFocusScope>().Instance;
        fixture.Commands.Clear();
        fixture.Enqueue(ModelRow(new EquipmentModel { Id = 8, EquipmentTypeId = 5, Name = "Другая модель" }),
            Roles(Роли.Специалист_по_инвентаризации_оборудования), Usage());
        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/ModelEquipmentForm?id=8"));
        page.WaitForAssertion(() =>
        {
            var current = page.FindComponent<ClayEditorFocusScope>().Instance;
            Assert.NotSame(first, current);
            Assert.True(current.Ready);
            Assert.Equal(8, CurrentModel(page).Id);
        });
        Assert.NotEmpty(context.JSInterop.Invocations["detach"]);
        Assert.Equal(0, fixture.WriteCount);
    }
}
