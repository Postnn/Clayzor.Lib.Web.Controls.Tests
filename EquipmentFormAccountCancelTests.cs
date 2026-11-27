using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Изменение только пароля требует решения и защищает поздний ввод без сериализации секрета.</summary>
    [Theory]
    [InlineData("accepted")]
    [InlineData("denied")]
    [InlineData("stale")]
    public async Task AccountResetAdoptsCredentialOnlyWithCurrentSecureRevision(string outcome)
    {
        const string originalPassword = "fixture-opening-password";
        const string editedPassword = "fixture-local-password";
        const string freshPassword = "fixture-fresh-password";
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var row = new EquipmentAccount { EquipmentId = 7, Login = "editor", Notes = "old" };
        fixture.Service.StoredAccounts.Add(row);
        fixture.Service.AccountOpeningSnapshot = new() { EquipmentId = 7, Login = "editor", Notes = "old", Password = originalPassword, ExpectedVersion = "v1" };
        fixture.Service.AccountFreshSnapshot = new() { EquipmentId = 7, Login = "editor", Notes = "fresh", Password = freshPassword, ExpectedVersion = "v2" };
        fixture.Service.DenyAccountReset = outcome == "denied";
        var metadata = new SensitiveModule(outcome == "stale");
        context.Services.AddSingleton<IJSRuntime>(new SensitiveRuntime(fixture.Js, metadata));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var added = TrackNextDialog(context);
        var opening = InvokeHandler(page, "OpenAccountDialogAsync", row);
        var reference = await added.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var editor = dialogs.FindComponent<EquipmentAccountDialog>();
        editor.WaitForAssertion(() => Assert.Equal(originalPassword, editor.Instance.Password));
        await editor.InvokeAsync(() => editor.Find("input[type='password']").InputAsync(editedPassword));
        var cancel = editor.InvokeAsync(() => editor.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(outcome == "accepted" ? freshPassword : editedPassword, editor.Instance.Password);
        Assert.Equal(outcome == "accepted" ? "v2" : "v1", editor.Instance.OriginalVersion);
        var replacements = context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"];
        Assert.Equal(outcome == "denied" ? 0 : 1, replacements.Count);
        foreach (var call in replacements.Concat(context.JSInterop.Invocations["clayDraftStore.saveTyped"]))
            foreach (var value in call.Arguments.OfType<string>())
            {
                Assert.DoesNotContain(originalPassword, value);
                Assert.DoesNotContain(editedPassword, value);
                Assert.DoesNotContain(freshPassword, value);
            }
        await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(4));
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal EquipmentAccount? AccountOpeningSnapshot { get; set; }
        internal EquipmentAccount? AccountFreshSnapshot { get; set; }
        internal bool DenyAccountReset { get; set; }

        public override Task<EquipmentAccount> GetAccountEditSnapshotAsync(int equipmentId, string login) =>
            AccountOpeningSnapshot is { } snapshot ? Task.FromResult(snapshot) : base.GetAccountEditSnapshotAsync(equipmentId, login);

        public override Task<EquipmentAccount?> ReadAccountEditorResetAsync(int equipmentId, string? originalLogin, int employeeId, CancellationToken ct)
        {
            Assert.Equal((7, "editor", 4), (equipmentId, originalLogin, employeeId));
            if (DenyAccountReset) throw new UnauthorizedAccessException("fixture");
            return Task.FromResult(AccountFreshSnapshot);
        }
    }

    private sealed class SensitiveModule(bool becomesStale) : IJSObjectReference
    {
        private int _checks;

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var json = identifier switch
            {
                "watchTransientSensitiveInputs" => "true",
                "readTransientSensitiveInputs" => """{"Revision":1,"Dirty":true,"Composing":false}""",
                "freezeTransientSensitiveInputs" => "1",
                "transientSensitiveFreezeIsCurrent" => ++_checks == 1 || !becomesStale ? "true" : "false",
                _ => null,
            };
            return new(json is null ? default! : JsonSerializer.Deserialize<T>(json)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SensitiveRuntime(IJSRuntime inner, IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            identifier == "import" && args is { Length: 1 } &&
            Equals(args[0], "./_content/Clayzor.Lib.Web.Controls/js/clayTransientSensitiveInputs.js")
                ? new((T)(object)module)
                : inner.InvokeAsync<T>(identifier, cancellationToken, args);
    }
}
