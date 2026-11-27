using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки общего lifecycle на production компоненте PhoneCode.</summary>
public sealed class PhoneCodeEditorCancelTests
{
    /// <summary>Три исхода используют только родительскую область и сохраняют строковый ключ.</summary>
    [Theory]
    [InlineData("Продолжить редактирование", false, false)]
    [InlineData("Закрыть без сохранения", true, false)]
    [InlineData("Отменить мои изменения и загрузить из БД", false, true)]
    public async Task Cancel_ChangesOnlyAcceptedScope(string choice, bool closes, bool replaces)
    {
        await using var fixture = new PhoneCodeFixture(existing: true);
        if (choice.StartsWith("Отменить"))
            fixture.QueueFresh(existing: true);
        await fixture.ChooseAsync(choice);

        Assert.Equal(closes, fixture.Invocations("clayModelEquipmentForm.close") > 0);
        if (closes)
            Assert.Equal(19L, Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.closeRam"]).Arguments[1]);
        else
            Assert.Equal(0, fixture.Invocations("clayDraftStore.closeRam"));
        var replacements = fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        Assert.Equal(replaces ? 1 : 0, replacements.Count);
        if (replaces)
        {
            var edit = Assert.Single(replacements);
            Assert.Equal("main", edit.Arguments[1]);
            Assert.Equal(19L, edit.Arguments[2]);
            using var snapshot = JsonDocument.Parse(Assert.IsType<string>(edit.Arguments[3]));
            Assert.Equal("00127", snapshot.RootElement.GetProperty("Version").GetProperty("Key").GetString());
            Assert.Equal("fresh", snapshot.RootElement.GetProperty("Current").GetProperty("Comment").GetString());
            Assert.Equal("fresh", fixture.Draft!.Comment);
            Assert.Null(fixture.Draft.SearchEpoch);
            Assert.False(snapshot.RootElement.GetProperty("Current").TryGetProperty("SearchEpoch", out _));
        }
        else if (!closes)
            Assert.Equal("local", fixture.Draft!.Comment);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Equal(0, fixture.Invocations("clayDraftStore.beginOperationIntent"));
    }

    /// <summary>Сбой чтения, другой владелец и новая raw-ревизия сохраняют исходный ввод.</summary>
    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("no-right")]
    [InlineData("missing")]
    [InlineData("late-raw")]
    public async Task Reset_WhenReadOrExactRevisionFails_KeepsLocalInput(string failure)
    {
        await using var fixture = new PhoneCodeFixture(existing: true, replacementSucceeds: false);
        fixture.QueueFresh(existing: true, failure: failure);

        await fixture.ChooseAsync("Отменить мои изменения и загрузить из БД");

        Assert.Equal("local", fixture.Draft!.Comment);
        Assert.Equal(0, fixture.Invocations("clayModelEquipmentForm.close"));
        Assert.Equal(failure == "late-raw" ? 1 : 0, fixture.Invocations("clayDraftStore.replaceEditorScope"));
        Assert.Equal(0, fixture.Invocations("clayDraftStore.beginOperationIntent"));
    }

    /// <summary>Новый номер сбрасывается к defaults исходного маршрута, оставаясь открытым.</summary>
    [Fact]
    public async Task Reset_NewPhoneUsesDefaults()
    {
        await using var fixture = new PhoneCodeFixture(existing: false);
        fixture.QueueFresh(existing: false);
        await fixture.ChooseAsync("Отменить мои изменения и загрузить из БД");

        Assert.Empty(fixture.Draft!.Comment);
        Assert.Empty(fixture.Draft!.Code);
        Assert.Equal(0, fixture.Invocations("clayModelEquipmentForm.close"));
        Assert.Equal(1, fixture.Invocations("clayDraftStore.replaceEditorScope"));
    }

    /// <summary>При Unknown закрытие оставляет операцию и родительскую область.</summary>
    [Fact]
    public async Task UnknownClose_DoesNotDiscardOrCommit()
    {
        await using var fixture = new PhoneCodeFixture(existing: true, unknown: true);
        await fixture.ApproveUnknownCloseAsync();

        Assert.Equal(1, fixture.Invocations("clayModelEquipmentForm.close"));
        Assert.Equal(19L, Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.closeRam"]).Arguments[1]);
        Assert.Equal(0, fixture.Invocations("clayDraftStore.discard"));
        Assert.Equal(0, fixture.Invocations("clayDraftStore.replaceEditorScope"));
        Assert.Equal(0, fixture.Invocations("clayDraftStore.commitOperation"));
        Assert.Equal(0, fixture.Invocations("clayDraftStore.beginOperationIntent"));
    }

    /// <summary>Старые метаданные принимаются только для точного открытого строкового ключа.</summary>
    [Theory]
    [InlineData("00127", true)]
    [InlineData("127", false)]
    public async Task LegacyEvidenceMustMatchExactVisibleCodeBeforeRam(string code, bool accepted)
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { Source = code, Target = code });
        await using var fixture = new PhoneCodeFixture(existing: true,
            evidence: [new(id.ToString("N"), "phone-code.update", payload)], acceptsEvidence: accepted);
        var js = fixture.Context.JSInterop;
        var begin = Assert.Single(js.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]);
        Assert.Equal(new object?[] { "phone-code-form", "existing:00127", "4" }, begin.Arguments.Take(3));
        Assert.Equal(begin.Arguments[3], Assert.Single(js.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]).Arguments[0]);
        Assert.Equal(accepted ? 1 : 0, fixture.Invocations("clayDraftStore.registerRam"));
        Assert.Equal(0, fixture.Invocations("clayDraftStore.beginOperationIntent"));
        if (accepted)
        {
            var at = fixture.Connection.CommandLog.IndexOf(SQLQueries.INSERT_ClayzorLegacyOperationEvidence);
            Assert.True(at >= 0);
            Assert.Equal(id, fixture.Connection.ParameterLog[at]["AttemptId"]);
            Assert.Equal("phone-code/00127", fixture.Connection.ParameterLog[at]["ConflictKey"]);
        }
        else
            Assert.DoesNotContain(SQLQueries.INSERT_ClayzorLegacyOperationEvidence, fixture.Connection.CommandLog);
    }

    /// <summary>Обновление с несохранённым вводом не заменяет родительскую ревизию.</summary>
    [Fact]
    public async Task Refresh_DirtyEditorKeepsInputAndSearchEpoch()
    {
        await using var fixture = new PhoneCodeFixture(existing: true);
        fixture.QueueFresh(existing: true, initial: true);
        await fixture.InvokeAsync("RefreshAsync");

        Assert.Equal("local", fixture.Draft!.Comment);
        Assert.Equal("fixture-epoch", fixture.Draft.SearchEpoch);
        Assert.Equal(0, fixture.Invocations("clayDraftStore.discard"));
        Assert.Equal(0, fixture.Invocations("clayDraftStore.replaceEditorScope"));
    }

    private sealed class PhoneCodeFixture : IAsyncDisposable
    {
        private readonly IRenderedComponent<PhoneCodeEntityForm> _form;
        private readonly IRenderedComponent<MudDialogProvider> _dialogs;
        private readonly Queue<ScriptEntry> _script = new();
        internal ScriptedConnection Connection { get; }
        internal BunitContext Context { get; } = new();

        internal PhoneCodeFixture(bool existing, bool replacementSucceeds = true, bool unknown = false,
            ClayLegacyDraftOperationEvidence[]? evidence = null, bool acceptsEvidence = true)
        {
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            DapperColumnMapper.Initialize();
            QueueFresh(existing, initial: true, importEvidence: evidence is { Length: > 0 } && acceptsEvidence);
            Connection = new ScriptedConnection(_script, emulateEquipmentIdentity: false);
            Context.Services.AddSingleton(_ => new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => evidence is { Length: > 0 } ? new EquipmentAccountTransactionConnection(Connection) : Connection));
            Context.Services.AddSingleton<AuthenticationStateProvider, PhoneCodeAuthentication>();
            Context.Services.AddSingleton<ClayDraftService>();
            Context.Services.AddSingleton<ClayFormWindowService>();
            Context.Services.AddSingleton<ClaySelectAdvancedSearchService>();
            Context.Services.AddMudServices();
            Context.Render<MudPopoverProvider>();
            _dialogs = Context.Render<MudDialogProvider>();
            var original = new PhoneCodeEntityForm.FormState(existing ? 7 : null, existing ? "00127" : "", existing ? "3" : null,
                existing ? 0 : null, existing ? "baseline" : "");
            var snapshot = JsonSerializer.Serialize(new
            {
                Original = original,
                Current = original with { Comment = "local", SearchEpoch = "fixture-epoch" },
                Version = existing ? new { Key = "00127", ChangedBy = 4, ChangedByName = "fixture", ChangedAt = DateTime.UnixEpoch } : null,
            });
            var operation = unknown
                ? """{"version":1,"attemptId":"fixture-attempt","kind":"phone-code.update","status":"unknown","revision":9,"data":"fixture","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}"""
                : null;
            Context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
                .SetResult(evidence ?? []);
            Context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(true);
            Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
                .SetResult(new ClayDraftRestore("phone-card", true, 19, null, operation,
                    new Dictionary<string, string> { ["main"] = snapshot, ["child-retained"] = "{}" }));
            Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true).SetResult(new ClayDraftRawApplyResult([], []));
            Context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
                .SetResult(new ClayDraftRestoreReadiness(true, [], []));
            Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
                .SetResult(new ClayDraftEditorScopeState(19, true, unknown));
            Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(replacementSucceeds);
            Context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(20);

            _form = existing ? Context.Render<ExistingPhoneCode>() : Context.Render<PhoneCodeEntityForm>();
            if (acceptsEvidence)
            {
                _form.WaitForAssertion(() => Assert.NotEmpty(_form.FindAll("[data-draft-scope='main']")));
                _form.WaitForAssertion(() => Assert.Single(Context.JSInterop.Invocations["clayDraftStore.getRestoreReadiness"]));
            }
            else
                _form.WaitForAssertion(() => Assert.Single(Context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]));
        }

        internal void QueueFresh(bool existing, string? failure = null, bool initial = false, bool importEvidence = false)
        {
            _script.Enqueue(Script.Rows(Script.Row(("КодСотрудника", failure == "wrong-owner" ? 8 : 4), ("Сотрудник", "fixture"))));
            _script.Enqueue(Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))));
            if (failure == "wrong-owner") return;
            _script.Enqueue(Script.Rows(Script.Row(("TerritoryUpdate", false), ("PhoneInsert", true),
                ("PhoneUpdate", failure != "no-right"), ("PhoneDelete", true))));
            if (failure == "no-right") return;
            if (existing)
            {
                if (failure == "missing")
                {
                    _script.Enqueue(Script.Rows());
                    return;
                }
                _script.Enqueue(Script.Rows(Script.Row(("Code", "00127"), ("TerritoryId", 7), ("Length", 3), ("TypeId", 0),
                    ("Comment", initial ? "baseline" : "fresh"), ("ChangedBy", 4), ("ChangedByName", "fixture"),
                    ("ChangedAt", initial ? DateTime.UnixEpoch : DateTime.UnixEpoch.AddMinutes(1)))));
                _script.Enqueue(Territory());
            }
            _script.Enqueue(Script.Rows());
            if (initial)
            {
                _script.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "fixture"))));
                _script.Enqueue(Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))));
                _script.Enqueue(Script.Rows(Script.Row(("PhoneInsert", true), ("PhoneUpdate", true), ("PhoneDelete", true))));
                _script.Enqueue(Script.Rows());
                _script.Enqueue(Script.Rows(Script.Row(("Pending", false))));
            }
            if (importEvidence)
            {
                var actor = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "fixture")));
                var role = Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0)));
                var access = Script.Rows(Script.Row(("PhoneInsert", true), ("PhoneUpdate", true), ("PhoneDelete", true)));
                foreach (var entry in new[]
                {
                    actor, role, access, Script.Rows(Script.Row(("Code", "00127"))),
                    Script.Rows(), Script.Rows(), Script.Rows(Script.Row(("Owned", true))),
                    actor, role, access, Script.Rows(), Script.Rows(Script.Row(("Pending", true))),
                })
                    _script.Enqueue(entry);
            }
            if (existing && initial) _script.Enqueue(Territory());
        }

        internal int Invocations(string identifier) => Context.JSInterop.Invocations[identifier].Count;
        internal PhoneCodeEntityForm.FormState? Draft =>
            (PhoneCodeEntityForm.FormState?)typeof(TerritorialEntityCardBase<PhoneCodeEntityForm.FormState>)
                .GetProperty("Draft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_form.Instance);

        internal Task InvokeAsync(string method)
        {
            var handler = typeof(TerritorialEntityCardBase<PhoneCodeEntityForm.FormState>)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return _form.InvokeAsync(() => (Task)handler.Invoke(_form.Instance, null)!);
        }

        internal async Task ApproveUnknownCloseAsync()
        {
            var cancelling = InvokeAsync("CancelAsync");
            _dialogs.WaitForAssertion(() => Assert.Single(_dialogs.FindComponents<ConfirmDialog>()));
            await _dialogs.InvokeAsync(() => _dialogs.FindComponent<ConfirmDialog>().FindAll("button")
                .Single(button => button.TextContent.Trim() == "Да").ClickAsync());
            await cancelling.WaitAsync(TimeSpan.FromSeconds(3));
        }

        internal async Task ChooseAsync(string choice)
        {
            var method = typeof(TerritorialEntityCardBase<PhoneCodeEntityForm.FormState>)
                .GetMethod("CancelAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var cancelling = _form.InvokeAsync(() => (Task)method.Invoke(_form.Instance, null)!);
            _dialogs.WaitForAssertion(() => Assert.Single(_dialogs.FindComponents<ClayEditorCancelDialog>()));
            await _dialogs.InvokeAsync(() => _dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
                .Single(button => button.TextContent.Trim() == choice).ClickAsync());
            await cancelling.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            Connection.Dispose();
        }

        private static ScriptEntry Territory() => Script.Rows(Script.Row(("Id", 7), ("Type", 3), ("Name", "fixture territory")));
    }

    private sealed class ExistingPhoneCode : PhoneCodeEntityForm
    {
        protected override void OnInitialized() => QueryId = "00127";
    }

    private sealed class PhoneCodeAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "fixture"))));
    }
}
