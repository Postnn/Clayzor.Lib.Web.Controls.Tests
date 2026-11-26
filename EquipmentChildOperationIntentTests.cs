using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    [Fact]
    public async Task AccountingIntentAndDispatchUseOriginalInputWhileBeginAwaits()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canUseAccounting", true);
        var draft = new EquipmentAccounting { Notes = "Отправленное значение" };
        fixture.Js.DeferredIdentifier = "clayDraftStore.beginOperationIntent";

        var saving = InvokeHandler(page, "SaveAccountingDraftAsync", draft);
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        draft.Notes = "Более поздний ввод";
        fixture.Js.Completion.SetResult();
        await saving.WaitAsync(TimeSpan.FromSeconds(3));

        using var envelope = ReadChildOperationEnvelope(context);
        Assert.Equal("Отправленное значение", envelope.RootElement.GetProperty("Intent").GetProperty("Notes").GetString());
        Assert.Equal("Отправленное значение", fixture.Service.LastAccountingDraft?.Notes);
        Assert.Equal("Более поздний ввод", draft.Notes);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
    }

    [Fact]
    public async Task AccountIntentContainsOpaqueProofAndKnownCleanupNeverSendsSecondWrite()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var failCleanup = true;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ => failCleanup)
            .SetException(new JSException("Тестовый отказ завершения журнала"));
        const string secret = "fixture-secret-not-for-journal";

        await InvokeHandler(page, "SaveAccountDraftAsync", null,
            new EquipmentAccountDialogResult("test-login", secret, true, "Тестовая запись"));

        using var envelope = ReadChildOperationEnvelope(context);
        var intent = envelope.RootElement.GetProperty("Intent");
        Assert.Equal("test-login", intent.GetProperty("Login").GetString());
        Assert.Equal(64, intent.GetProperty("SecretProof").GetString()!.Length);
        Assert.DoesNotContain(secret, envelope.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.False(intent.TryGetProperty("Password", out _));
        Assert.Equal(1, fixture.Service.AddAccountCalls);
        var operation = Assert.IsType<ClayDraftOperation>(GetPrivate(page.Instance, "_draftOperation"));
        Assert.Equal(ClayDraftOperationStatus.Confirmed, operation.Status);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        failCleanup = false;
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Equal(1, fixture.Service.AddAccountCalls);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.commitOperation"].Count);
        Assert.False(PrivateBool(page.Instance, "_draftOperationUnresolved"));
    }

    private static JsonDocument ReadChildOperationEnvelope(BunitContext context)
    {
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var intent = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        return JsonDocument.Parse(intent.RootElement.GetProperty("data").GetString()!);
    }
}
