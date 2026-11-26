using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private static readonly ClayDraftRecoveryCandidate OrphanFixture = new("orphan-draft", 42, true, true);

    /// <summary>Чистая регистрация остаётся пустой, а чужая операция в том же контексте блокирует Begin.</summary>
    [Fact]
    public async Task OrphanRecovery_EmptyAttachDoesNotWriteAndBlocksBegin()
    {
        var fixture = CreateFixture(phoneConfig: true);
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true).SetResult([OrphanFixture]);
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("незавершённая операция", page.Find("[data-testid=equipment-recovery]").TextContent));

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTyped"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeHandler(page, "BeginDraftOperationAsync", "equipment-save", null));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Equal(0, fixture.Service.SyncCalls);
    }

    /// <summary>Сохранённая попытка открывается явным выбором без повторной бизнес-команды.</summary>
    [Fact]
    public async Task OrphanRecovery_ExplicitAdoptionOpensUnknownWithoutReplay()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var candidates = new[] { OrphanFixture };
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => candidates.Length > 0)
            .SetResult([OrphanFixture]);
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=equipment-recovery]")));
        var input = OrphanState(page.Instance);
        input["Model"]!["DocumentWriteOffId"] = 91;
        var adoption = context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.recoverListedDraft", _ => true)
            .SetResult(OrphanRestore(input.ToJsonString()));

        await page.InvokeAsync(() => page.FindAll("[data-testid=equipment-recovery] button").Single(button => button.TextContent.Trim() == "Открыть черновик").ClickAsync());
        candidates = [];
        Assert.Equal("orphan-draft", Assert.Single(adoption.Invocations).Arguments[1]);
        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));
        Assert.Equal(91, CurrentModel(page.Instance).DocumentWriteOffId);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    /// <summary>Ввод, пришедший после запуска adoption, не заменяет и не перезаписывает сохранённый orphan.</summary>
    [Fact]
    public async Task OrphanRecovery_LateLocalInputKeepsContextsSeparate()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true).SetResult([OrphanFixture]);
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=equipment-recovery]")));
        var input = OrphanState(page.Instance);
        input["Model"]!["DocumentWriteOffId"] = 91;
        var adoption = context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.recoverListedDraft", _ => true);
        var opening = InvokeHandler(page, "OpenEquipmentRecoveryAsync", OrphanFixture);
        try
        {
            page.WaitForAssertion(() => Assert.Single(adoption.Invocations));
            await page.InvokeAsync(() => CurrentModel(page.Instance).DocumentWriteOffId = 99);
        }
        finally { adoption.SetResult(OrphanRestore(input.ToJsonString())); }
        await opening.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(99, CurrentModel(page.Instance).DocumentWriteOffId);
        Assert.True(PrivateBool(page.Instance, "_orphanAdoptionConflicted"));
        await InvokeHandler(page, "PersistDraftAsync");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTyped"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    /// <summary>Снятое право блокирует открытие даже при прежней локальной роли.</summary>
    [Fact]
    public async Task OrphanRecovery_RevokedRoleCannotAdopt()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true).SetResult([OrphanFixture]);
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=equipment-recovery]")));
        fixture.Service.Roles.Remove(43);

        await InvokeHandler(page, "OpenEquipmentRecoveryAsync", OrphanFixture);

        Assert.Contains("Не удалось подтвердить сотрудника", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recoverListedDraft"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Явное открытие устаревшей исходной версии сохраняет попытку без слияния.</summary>
    [Fact]
    public async Task OrphanRecovery_StaleOriginalIsNotMerged()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true).SetResult([OrphanFixture]);
        var page = context.Render<EquipmentForm>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=equipment-recovery]")));
        var input = OrphanState(page.Instance);
        input["OriginalSnapshot"] = "different-fixture-version";
        input["Model"]!["DocumentWriteOffId"] = 91;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.recoverListedDraft", _ => true)
            .SetResult(OrphanRestore(input.ToJsonString()));

        await InvokeHandler(page, "OpenEquipmentRecoveryAsync", OrphanFixture);

        Assert.NotEqual(91, CurrentModel(page.Instance).DocumentWriteOffId);
        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));
        Assert.Contains("Исходная версия изменилась", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTyped"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Begin просит атомарную проверку соседних операций после сканирования страницы.</summary>
    [Fact]
    public async Task EquipmentBegin_UsesAtomicCrossDraftGuard()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await InvokeHandler(page, "BeginDraftOperationAsync", "equipment-save", null);

        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var intent = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.True(intent.RootElement.GetProperty("blockOtherDraftOperations").GetBoolean());
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    private static JsonObject OrphanState(EquipmentForm form)
    {
        var capture = typeof(EquipmentForm).GetMethod("SerializeDraftState",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return JsonNode.Parse((string)capture.Invoke(form, null)!)!.AsObject();
    }

    private static ClayDraftRestore OrphanRestore(string state) => new("test-registration", true, 42, state,
        """{"version":1,"attemptId":"orphan-attempt","kind":"equipment-save","status":"unknown","revision":42,"data":"{}","scopes":[],"acceptLegacyTyped":true,"legacyTypedRevision":0}""");
}
