using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Восстановление исходного поиска для реальной формы телефонного кода.</summary>
public sealed class PhoneCodeSearchRecoveryTests
{
    /// <summary>Восстановленный запрос принимает только исходный сотрудник, маршрут и поле.</summary>
    [Theory]
    [InlineData(4, "new:", true, true)]
    [InlineData(4, "new:", false, false)]
    [InlineData(8, "new:", true, false)]
    [InlineData(4, "new:7", true, false)]
    public async Task RestoredTerritorySearch_RequiresOriginalActorAndRoute(int owner, string route, bool sameEpoch, bool resumes)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        DapperColumnMapper.Initialize();
        var script = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "fixture"))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))),
            Script.Rows(Script.Row(("TerritoryUpdate", false), ("PhoneInsert", true), ("PhoneUpdate", true), ("PhoneDelete", true))),
            Script.Rows(),
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "fixture"))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))),
            Script.Rows(Script.Row(("PhoneInsert", true), ("PhoneUpdate", true), ("PhoneDelete", true))),
            Script.Rows(),
            Script.Rows(Script.Row(("Pending", false))),
        ]);
        using var connection = new ScriptedConnection(script, emulateEquipmentIdentity: false);
        context.Services.AddSingleton(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        context.Services.AddSingleton<AuthenticationStateProvider, PhoneCodeAuthentication>();
        context.Services.AddSingleton<ClayDraftService>();
        context.Services.AddSingleton<ClayFormWindowService>();
        context.Services.AddSingleton<ClaySelectAdvancedSearchService>();
        context.Services.AddMudServices();
        context.Render<MudPopoverProvider>();

        var id = Guid.NewGuid().ToString("N");
        var proof = Guid.NewGuid().ToString("N");
        var epoch = Guid.NewGuid().ToString("N");
        var initial = new PhoneCodeEntityForm.FormState(null, "", null, null, "");
        var snapshot = JsonSerializer.Serialize(new { Original = initial, Current = initial with { SearchEpoch = sameEpoch ? epoch : null } });
        var lease = JsonSerializer.Serialize(new
        {
            Version = 1, Actor = owner, Route = route, PreviousTerritory = (int?)null, RequestId = id, Proof = proof, Epoch = epoch,
        });
        const string leaseScope = "phone-code.territory-search";
        var restore = new ClayDraftRestore("phone-code", true, 8, null, null,
            new Dictionary<string, string> { ["main"] = snapshot, [leaseScope] = lease });
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([]);
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true).SetResult(restore);
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true).SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<bool>("claySelectAdvancedSearch.resumeUndispatched", _ => true).SetResult(true);
        context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(11);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(11, false, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);

        var form = context.Render<PhoneCodeEntityForm>();
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("[data-draft-scope='main']")));
        var transfer = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]);
        Assert.Equal(new object?[] { "phone-code-form", "new", "4" }, transfer.Arguments.Take(3));
        Assert.Equal(transfer.Arguments[3],
            Assert.Single(context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]).Arguments[0]);
        Assert.Equal("new:", Assert.Single(context.JSInterop.Invocations["clayDraftStore.registerRam"]).Arguments[1]);
        // Проверяем завершившийся цикл готовности, в котором форма может вызвать перепривязку.
        await form.InvokeAsync(() => Task.CompletedTask);

        if (resumes)
        {
            form.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["claySelectAdvancedSearch.resumeUndispatched"]));
            var invocation = Assert.Single(context.JSInterop.Invocations["claySelectAdvancedSearch.resumeUndispatched"]);
            Assert.Equal(id, invocation.Arguments[1]);
            Assert.Equal(proof, invocation.Arguments[2]);
            var service = context.Services.GetRequiredService<ClaySelectAdvancedSearchService>();
            Assert.True(await service.ReadyForSelectionAsync(id, 1));
            script.Enqueue(Script.Rows(Script.Row(("Id", 7), ("Type", 3), ("Name", "fixture territory"))));
            // ClaySelect перечитывает выбранную подпись после обновления родительского значения.
            script.Enqueue(Script.Rows(Script.Row(("Id", 7), ("Type", 3), ("Name", "fixture territory"))));
            Assert.Equal("Accepted", await service.ApplyAsync(id, 1, JsonSerializer.SerializeToElement(7)));
            form.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
                call => Equals("main", call.Arguments[1]) && call.Arguments[2] is string json && json.Contains("\"TerritoryId\":7")));
            Assert.Equal("Accepted", await service.ApplyAsync(id, 1, JsonSerializer.SerializeToElement(7)));
        }
        else
            Assert.Empty(context.JSInterop.Invocations["claySelectAdvancedSearch.resumeUndispatched"]);
        Assert.Empty(context.JSInterop.Invocations["claySelectAdvancedSearch.open"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.TrimStart().StartsWith("INSERT ") || sql.TrimStart().StartsWith("UPDATE "));
    }

    private sealed class PhoneCodeAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "fixture"))));
    }
}
