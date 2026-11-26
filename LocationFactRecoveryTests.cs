using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Controls.Themes;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Layout;
using Kesco.App.Web.Inventory.Components.Pages.Inv;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Восстановление операции через рабочую форму инвентаризации.</summary>
public sealed class LocationFactRecoveryTests
{
    private const string WikiBase = "https://docs.example/wiki?topic=";
    private const string HelpUrl = WikiBase + "Inventory%2FInv%2FLocationFact.aspx";
    private const string Notes = "Проверено в fixture";
    private static readonly LocationFactSnapshot Snapshot = new(new(10, "Тестовый склад", true),
        new(17, 4, "Владелец", "Тестовое устройство", "", 10, "Тестовый склад", null, new DateTime(2026, 11, 20)),
        null, false, "");

    /// <summary>После подтверждения БД повтор браузерного commit использует ту же ревизию без INSERT.</summary>
    [Fact]
    public async Task ConfirmedFactRetriesOnlyCleanupAndUnblocksNextEquipment()
    {
        using var db = new FactConnection();
        await using var context = Configure(db);
        var first = true;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!first) return false;
            first = false;
            return true;
        }).SetException(new JSException("Тестовый отказ browser cleanup"));
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await WaitForButton(page, dialogs, "Подтвердить").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, db.Inserts);
        Assert.Equal(1, db.Commits);
        Assert.Equal(Notes, db.SentNotes);
        Assert.Equal(1, db.SentBroken);
        Assert.Contains("Проверка записана", page.Markup);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var wire = JsonDocument.Parse((string)begin.Arguments[1]!);
        using var intent = JsonDocument.Parse(wire.RootElement.GetProperty("data").GetString()!);
        Assert.Equal(Notes, intent.RootElement.GetProperty("Notes").GetString());
        Assert.True(intent.RootElement.GetProperty("Broken").GetBoolean());
        Assert.False(intent.RootElement.GetProperty("Move").GetBoolean());
        Assert.Equal(17, intent.RootElement.GetProperty("Expected").GetProperty("Equipment").GetProperty("EquipmentId").GetInt32());
        await WaitForButton(page, dialogs, "Завершить обработку черновика").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, db.Inserts);
        var receipt = Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Equal(6L, receipt.Arguments[1]);
        var calls = context.JSInterop.Invocations.Select(call => call.Identifier).ToList();
        Assert.True(calls.IndexOf("clayDraftStore.recordConfirmedOperation") < calls.IndexOf("clayDraftStore.commitOperation"));
        var commits = context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        Assert.Equal(2, commits.Count);
        Assert.All(commits, call => Assert.Equal(6L, call.Arguments[1]));
        Assert.False(WaitForButton(page, dialogs, "Тестовое устройство").HasAttribute("disabled"));
    }

    /// <summary>Если запись подтверждения временно недоступна, повтор начинает с неё и блокирует очистку.</summary>
    [Fact]
    public async Task ReceiptFailureDoesNotCleanOrRepeatFact()
    {
        using var db = new FactConnection();
        await using var context = Configure(db);
        var first = true;
        context.JSInterop.SetupVoid("clayDraftStore.recordConfirmedOperation", _ =>
        {
            if (!first) return false;
            first = false;
            return true;
        }).SetException(new JSException("Тестовый отказ записи подтверждения"));
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await WaitForButton(page, dialogs, "Подтвердить").ClickAsync(new MouseEventArgs());

        Assert.Contains("Проверка записана", page.Markup);
        Assert.Equal(1, db.Inserts);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        await WaitForButton(page, dialogs, "Завершить обработку черновика").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, db.Inserts);
        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"].Count);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }

    /// <summary>После повторного открытия подтверждение возобновляет очистку без повторной записи.</summary>
    [Fact]
    public async Task ConfirmedReceiptRestoresCleanupOnlyAfterRemount()
    {
        using var db = new FactConnection();
        var command = JsonSerializer.Serialize(new { Expected = Snapshot, Notes, Broken = true, Move = false });
        var receipt = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "previous-fact", kind = "confirm-location", status = "confirmed", revision = 6,
            data = command, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = Configure(db, restoredOperation: receipt);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        page.WaitForAssertion(() => Assert.Contains("Проверка записана", page.Markup));
        Assert.DoesNotContain("Исход попытки", page.Markup);
        Assert.Empty(dialogs.FindAll("button").Where(b => b.TextContent.Trim() == "Подтвердить"));

        await WaitForButton(page, dialogs, "Завершить обработку черновика").ClickAsync(new MouseEventArgs());

        Assert.Equal(0, db.Inserts);
        var cleanup = Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(6L, cleanup.Arguments[1]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }

    /// <summary>Ошибка ответа Begin не запускает SQL, повтор исправляет только известную неотправленную попытку.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostBeginAcknowledgementRepairsStorageWithoutFacts(bool anotherAttempt)
    {
        using var db = new FactConnection();
        await using var context = Configure(db, failBegin: true);
        if (anotherAttempt)
            context.JSInterop.Setup<string?>("clayDraftStore.retryStorage", _ => true)
                .SetResult("""{"version":1,"attemptId":"another-fact","kind":"confirm-location","status":"unknown","revision":8,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await WaitForButton(page, dialogs, "Подтвердить").ClickAsync(new MouseEventArgs());
        Assert.Contains("Команда не отправлена", page.Markup);
        Assert.Equal(0, db.Inserts);

        var invocation = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var wire = JsonDocument.Parse((string)invocation.Arguments[1]!);
        var unsent = wire.RootElement.GetProperty("attemptId").GetString();
        Assert.True(Guid.TryParse(unsent, out _));
        await WaitForButton(page, dialogs, "Повторить сохранение черновика").ClickAsync(new MouseEventArgs());
        Assert.Equal(unsent, Assert.Single(context.JSInterop.Invocations["clayDraftStore.retryStorage"]).Arguments[1]);
        Assert.Equal(0, db.Inserts);
        Assert.Equal(anotherAttempt, WaitForButton(page, dialogs, "Подтвердить").HasAttribute("disabled"));
        if (anotherAttempt) Assert.Contains("another-fact", page.Markup);
    }

    /// <summary>Потеря ответа после INSERT допускает сверку и сохраняет блокировку повтора.</summary>
    [Fact]
    public async Task TrueUnknownAllowsReadOnlyComparisonWithoutSecondFact()
    {
        using var db = new FactConnection { LoseInsertResponse = true };
        await using var context = Configure(db);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await WaitForButton(page, dialogs, "Подтвердить").ClickAsync(new MouseEventArgs());

        Assert.Contains("fact-test-attempt", page.Markup);
        Assert.Equal(1, db.Inserts);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        var checkbox = BrokenControl(page, dialogs);
        var notes = NotesControl(page, dialogs);
        Assert.True(checkbox.Disabled);
        Assert.True(notes.Options!.ReadOnly);
        var before = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count;
        await page.InvokeAsync(() => checkbox.StateChanged.InvokeAsync(false));
        await page.InvokeAsync(() => notes.ValueChanged.InvokeAsync("Поздний ввод"));
        Assert.True(BrokenControl(page, dialogs).State);
        Assert.Equal(Notes, NotesControl(page, dialogs).Value);
        Assert.Equal(before, context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count);
        await WaitForButton(page, dialogs, "Сверить с базой").ClickAsync(new MouseEventArgs());
        Assert.True(WaitForButton(page, dialogs, "Подтвердить").HasAttribute("disabled"));
        Assert.Equal(1, db.Inserts);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
    }

    /// <summary>Запоздавшие события предыдущего окна не меняют следующий черновик после отмены.</summary>
    [Fact]
    public async Task LateCallbacksCannotChangeNextEditorAfterCancel()
    {
        using var db = new FactConnection();
        await using var context = Configure(db);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        WaitForButton(page, dialogs, "Подтвердить");
        var oldBroken = BrokenControl(page, dialogs).StateChanged;
        var oldNotes = NotesControl(page, dialogs).ValueChanged;

        await ChooseFactCancelAsync(page, dialogs, "Закрыть без сохранения");
        await WaitForButton(page, dialogs, "Тестовое устройство").ClickAsync(new MouseEventArgs());
        WaitForButton(page, dialogs, "Подтвердить");
        var before = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count;

        await page.InvokeAsync(() => oldBroken.InvokeAsync(true));
        await page.InvokeAsync(() => oldNotes.InvokeAsync("Поздний ввод"));

        Assert.Equal(before, context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count);
        Assert.False(BrokenControl(page, dialogs).State);
        Assert.Equal("", NotesControl(page, dialogs).Value);

        await page.InvokeAsync(() => BrokenControl(page, dialogs).StateChanged.InvokeAsync(true));
        await page.InvokeAsync(() => NotesControl(page, dialogs).ValueChanged.InvokeAsync(Notes));
        Assert.True(BrokenControl(page, dialogs).State);
        Assert.Equal(Notes, NotesControl(page, dialogs).Value);
        var call = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last();
        using var draft = JsonDocument.Parse((string)call.Arguments[2]!);
        Assert.True(draft.RootElement.GetProperty("Broken").GetBoolean());
        Assert.Equal(Notes, draft.RootElement.GetProperty("Notes").GetString());
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Контекст рабочего места переживает отмену. Временный null и явная очистка различаются.</summary>
    [Fact]
    public async Task WorkplaceContextSurvivesCancelAndChangesOnlyOnExplicitClear()
    {
        using var db = new FactConnection();
        var workplace = JsonSerializer.Serialize(new { SiteId = 10, ResponsibleId = 4, ItOnly = true, Scanner = true });
        await using var context = Configure(db, workplaceJson: workplace);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        WaitForButton(page, dialogs, "Подтвердить");

        Assert.True(page.FindComponents<ClayCheckbox>().Single(c => c.Instance.Label == "IT-оборудование").Instance.State);
        Assert.True(page.FindComponents<ClayCheckbox>().Single(c => c.Instance.Label == "Режим ввода - сканер").Instance.State);
        Assert.Equal(4, db.ResponsibleOnLastList);
        await ChooseFactCancelAsync(page, dialogs, "Закрыть без сохранения");
        var discard = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal("main", discard.Arguments[1]);
        Assert.Null(discard.Arguments[3]);

        var location = page.FindComponent<ClaySelect<LocationLookup, int?>>();
        await page.InvokeAsync(() => location.Instance.ValueChanged.InvokeAsync((int?)null));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Where(call => Equals(call.Arguments[1], "workplace")));
        Assert.False(WaitForButton(page, dialogs, "Тестовое устройство").HasAttribute("disabled"));

        await page.InvokeAsync(() => location.Instance.Cleared.InvokeAsync());
        var saved = Assert.Single(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "workplace"));
        using var snapshot = JsonDocument.Parse((string)saved.Arguments[2]!);
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("SiteId").ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("ResponsibleId").ValueKind);
        Assert.True(snapshot.RootElement.GetProperty("ItOnly").GetBoolean());
        Assert.True(snapshot.RootElement.GetProperty("Scanner").GetBoolean());
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Сырой поиск без FactDraft ожидает доступного поля и восстанавливается без открытия подтверждения.</summary>
    [Fact]
    public async Task RawOnlyWorkplaceWaitsForSearchFieldWithoutDispatch()
    {
        using var db = new FactConnection();
        var searchReady = false;
        await using var context = Configure(db, includeEditor: false, searchReady: () => searchReady);
        var page = context.Render<LocationFact>();
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"],
            call => Equals(call.Arguments[1], "workplace")));
        Assert.Contains("Повторить восстановление поиска", page.Markup);

        searchReady = true;
        var location = page.FindComponent<ClaySelect<LocationLookup, int?>>();
        await page.InvokeAsync(() => location.Instance.ValueChanged.InvokeAsync((int?)10));
        page.WaitForAssertion(() => Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count(
            call => Equals(call.Arguments[1], "workplace"))));
        Assert.DoesNotContain("Повторить восстановление поиска", page.Markup);

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.openScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Старый raw без снимка оборудования сохраняется, а новый факт блокируется до миграции.</summary>
    [Fact]
    public async Task LegacyOrphanRawRemainsProtected()
    {
        using var db = new FactConnection();
        var workplace = JsonSerializer.Serialize(new { SiteId = 10, ItOnly = false, Scanner = false });
        await using var context = Configure(db, includeEditor: false, workplaceJson: workplace, legacyRaw: true);
        var page = context.Render<LocationFact>();
        page.WaitForAssertion(() => Assert.Contains("Черновик прежней версии", page.Markup));

        Assert.Equal(10, page.FindComponent<ClaySelect<LocationLookup, int?>>().Instance.Value);
        Assert.Contains("Черновик прежней версии", page.Markup);
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Trim() == "Тестовое устройство");
        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Показать сохранённый ввод")
            .ClickAsync(new MouseEventArgs());
        var fields = page.WaitForElements("[aria-label='Ввод из прежнего черновика'] [data-draft-key][readonly]", 4);
        string[] keys = ["fact.location", "fact.notes", "fact.responsible", "fact.search"];
        Assert.Equal(keys, fields.Select(field => field.GetAttribute("data-draft-key")).Order(StringComparer.Ordinal));
        Assert.All(fields, field => Assert.False(field.HasAttribute("disabled")));
        foreach (var key in keys) Assert.Single(page.FindAll($"[data-draft-key='{key}']"));
        Assert.Empty(page.FindAll("[data-draft-scope=workplace]"));
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"],
            call => Equals(call.Arguments[1], "main")));

        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Вернуться к рабочему месту")
            .ClickAsync(new MouseEventArgs());
        Assert.Equal(10, page.FindComponent<ClaySelect<LocationLookup, int?>>().Instance.Value);
        Assert.Empty(page.FindAll("[aria-label='Ввод из прежнего черновика']"));
        Assert.True(page.Find("[data-draft-scope=workplace]").HasAttribute("inert"));
        page.WaitForAssertion(() => Assert.True(context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count(
            call => Equals(call.Arguments[1], "workplace")) >= 2));
        Assert.Contains("Черновик прежней версии", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Три варианта отмены управляют только main, сохраняя режим сканера и workplace.</summary>
    [Theory]
    [InlineData("Продолжить редактирование", false)]
    [InlineData("Закрыть без сохранения", false)]
    [InlineData("Отменить мои изменения и загрузить из БД", false)]
    [InlineData("Отменить мои изменения и загрузить из БД", true)]
    public async Task CancelFact_AcceptsExactMainRevision(string choice, bool scanner)
    {
        using var db = new FactConnection();
        var workplace = JsonSerializer.Serialize(new { SiteId = 10, ResponsibleId = 4, ItOnly = true, Scanner = scanner });
        await using var context = Configure(db, workplaceJson: workplace, scannerEditor: scanner);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(19, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await ChooseFactCancelAsync(page, dialogs, choice);

        var calls = context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        if (choice == "Продолжить редактирование")
        {
            Assert.Empty(calls);
            Assert.Equal(Notes, NotesControl(page, dialogs).Value);
        }
        else
        {
            var replace = Assert.Single(calls);
            Assert.Equal("main", replace.Arguments[1]);
            Assert.Equal(19L, replace.Arguments[2]);
            if (choice == "Закрыть без сохранения")
                Assert.Null(replace.Arguments[3]);
            else
            {
                using var snapshot = JsonDocument.Parse(Assert.IsType<string>(replace.Arguments[3]));
                Assert.Equal(scanner, snapshot.RootElement.GetProperty("Scanner").GetBoolean());
                Assert.Equal("", snapshot.RootElement.GetProperty("Notes").GetString());
                Assert.False(snapshot.RootElement.GetProperty("Broken").GetBoolean());
                Assert.Empty(NotesControl(page, dialogs).Value);
            }
        }
        Assert.Equal(4, db.ResponsibleOnLastList);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.DoesNotContain(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "workplace"));
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Ошибка подготовки либо более свежая raw-ревизия удерживает ввод и текущий снимок.</summary>
    [Theory]
    [InlineData("actor")]
    [InlineData("equipment")]
    [InlineData("late")]
    public async Task CancelFact_ResetFailurePreservesPendingEditor(string failure)
    {
        using var db = new FactConnection();
        await using var context = Configure(db);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(19, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(false);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        WaitForButton(page, dialogs, "Отмена");
        if (failure == "actor") db.ActorId = 22;
        if (failure == "equipment") db.EquipmentId = 27;
        await ChooseFactCancelAsync(page, dialogs, "Отменить мои изменения и загрузить из БД");

        Assert.Equal(Notes, NotesControl(page, dialogs).Value);
        Assert.Equal(failure == "late" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        Assert.Equal(0, db.Inserts);
    }

    /// <summary>Unknown закрывает только окно либо остаётся на сверке без scoped CAS.</summary>
    [Theory]
    [InlineData("Закрыть без сохранения")]
    [InlineData("Отменить мои изменения и загрузить из БД")]
    public async Task UnknownFactCancel_PreservesAttemptAndInput(string choice)
    {
        using var db = new FactConnection();
        var data = JsonSerializer.Serialize(new { Expected = Snapshot, Notes, Broken = true, Move = false });
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "fact-unknown", kind = "confirm-location", status = "unknown", revision = 8,
            data, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = Configure(db, restoredOperation: operation);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        await ChooseFactCancelAsync(page, dialogs, choice);

        Assert.Contains("fact-unknown", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, db.Inserts);
    }

    private static async Task ChooseFactCancelAsync(IRenderedComponent<LocationFact> page,
        IRenderedComponent<MudDialogProvider> dialogs, string choice)
    {
        WaitForButton(page, dialogs, "Отмена");
        var cancellation = page.InvokeAsync(() => WaitForButton(page, dialogs, "Отмена").ClickAsync(new MouseEventArgs()));
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == choice).ClickAsync(new MouseEventArgs()));
        await cancellation.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static ClayCheckbox BrokenControl(IRenderedComponent<LocationFact> page, IRenderedComponent<MudDialogProvider> dialogs) =>
        page.FindComponents<ClayCheckbox>().Concat(dialogs.FindComponents<ClayCheckbox>())
            .First(component => component.Instance.Label == "Сломано, ремонту не подлежит").Instance;

    private static ClayTextArea NotesControl(IRenderedComponent<LocationFact> page, IRenderedComponent<MudDialogProvider> dialogs) =>
        page.FindComponents<ClayTextArea>().Concat(dialogs.FindComponents<ClayTextArea>())
            .First(component => component.Instance.Options?.Label == "Примечание").Instance;

    private static AngleSharp.Dom.IElement WaitForButton(IRenderedComponent<LocationFact> page,
        IRenderedComponent<MudDialogProvider> dialogs, string text)
    {
        IEnumerable<AngleSharp.Dom.IElement> Matching() => page.FindAll("button").Concat(dialogs.FindAll("button"))
            .Where(button => button.TextContent.Trim() == text);
        page.WaitForAssertion(() => Assert.NotEmpty(Matching()));
        return Matching().First();
    }

    [Theory]
    [InlineData("Inv/LocationFact")]
    [InlineData("Inv/LocationFact.aspx?equipment=17#notes")]
    public void WorkplaceAliasesResolveTheRegisteredOwnerDocumentation(string alias) =>
        Assert.Equal(HelpUrl, ClayFormHelpUrl.Resolve(WikiBase, InventoryFormHelp.Find(alias)));

    [Fact]
    public async Task NestedHelpAndThemeStayOutsideTheWorkplaceAndConfirmationLifecycle()
    {
        using var db = new FactConnection();
        var workplace = JsonSerializer.Serialize(new { SiteId = 10, ResponsibleId = 4, ItOnly = true, Scanner = true });
        IRenderedComponent<MudPopoverProvider>? popovers = null;
        await using var context = Configure(db, workplaceJson: workplace, onPopoversRendered: provider => popovers = provider);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<LocationFact>();
        WaitForButton(page, dialogs, "Подтвердить");
        var actions = dialogs.FindComponent<ClayFormActions>();
        var group = actions.Find("[role='group']");
        Assert.Null(group.Closest("[data-draft-scope]"));
        var help = group.QuerySelector("a[aria-label='Документация']")!;
        Assert.Equal(HelpUrl, help.GetAttribute("href"));
        Assert.Equal("_blank", help.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", help.GetAttribute("rel"));

        var rawCount = context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count;
        var typedCount = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count;
        var menu = actions.Find("button[aria-label='Тема оформления']");
        await menu.ClickAsync(new MouseEventArgs());
        var choice = popovers!.WaitForElement("[data-clay-theme-option='modern:light']");
        await choice.ClickAsync(new MouseEventArgs());
        Assert.Equal(ClayThemePreference.ModernLight, context.Services.GetRequiredService<ClayThemeState>().Preference);
        Assert.Equal(Notes, NotesControl(page, dialogs).Value);
        Assert.True(page.FindComponents<ClayCheckbox>().Single(c => c.Instance.Label == "Режим ввода - сканер").Instance.State);
        Assert.Equal(rawCount, context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count);
        Assert.Equal(typedCount, context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, db.Inserts);
    }

    private static BunitContext Configure(FactConnection connection, bool failBegin = false,
        bool includeEditor = true, string? workplaceJson = null, Func<bool>? searchReady = null, bool legacyRaw = false, string? restoredOperation = null,
        Action<IRenderedComponent<MudPopoverProvider>>? onPopoversRendered = null, bool scannerEditor = false)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var restored = JsonSerializer.Serialize(new { Snapshot, Notes, Broken = true, Scanner = scannerEditor, Conflict = false });
        var scopes = new Dictionary<string, string>();
        if (includeEditor) scopes["main"] = restored;
        if (workplaceJson is not null) scopes["workplace"] = workplaceJson;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("fact-test", true, 5, null, restoredOperation, scopes));
        bool SearchPending() => searchReady?.Invoke() == false;
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", call => SearchPending() && Equals(call.Arguments[1], "workplace"))
            .SetResult(new ClayDraftRawApplyResult([], ["data:fact.search"]));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", call => !SearchPending() || !Equals(call.Arguments[1], "workplace"))
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => SearchPending())
            .SetResult(new ClayDraftRestoreReadiness(false, ["workplace"], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => !SearchPending())
            .SetResult(new ClayDraftRestoreReadiness(!legacyRaw, legacyRaw ? ["main"] : [], []));
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(19, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        var begin = context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true);
        if (failBegin) begin.SetException(new JSException("Тестовая потеря ответа Begin"));
        else begin.SetResult("""{"version":1,"attemptId":"fact-test-attempt","kind":"confirm-location","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.ComponentFactories.AddStub<InventoryMainMenu>();
        DapperColumnMapper.Initialize();
        context.Services.AddScoped(_ => new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddSingleton(new Clayzor.Lib.Web.Settings.ClayAppSettings { UriWikiHelp = WikiBase });
        context.Services.AddSingleton(new ClayThemeState(NullLogger<ClayThemeState>.Instance, null));
        context.Services.AddScoped<LocationSelectDataSource>();
        context.Services.AddScoped<EmployeeSelectDataSource>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
        context.Services.AddMudServices();
        var popovers = context.Render<MudPopoverProvider>();
        onPopoversRendered?.Invoke(popovers);
        return context;
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class FactConnection : DbConnection
    {
        internal int ActorId { get; set; } = 4;
        internal int EquipmentId { get; set; } = 17;
        internal int Inserts { get; private set; }
        internal bool LoseInsertResponse { get; init; }
        internal int Commits { get; private set; }
        internal string? SentNotes { get; private set; }
        internal int SentBroken { get; private set; }
        internal int? ResponsibleOnLastList { get; private set; }
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => new Transaction(this, level);
        protected override DbCommand CreateDbCommand() => new Command(this);

        private sealed class Transaction(FactConnection owner, IsolationLevel level) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => level;
            protected override DbConnection DbConnection => owner;
            public override void Commit() => owner.Commits++;
            public override void Rollback() { }
        }

        private sealed class Command(FactConnection owner) : DbCommand
        {
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
            protected override DbParameter CreateDbParameter() => new FakeParameter();
            public override void Prepare() { }
            public override void Cancel() { }
            public override object ExecuteScalar() => throw new NotSupportedException();
            public override int ExecuteNonQuery() => throw new NotSupportedException();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (CommandText.Contains("SUSER_SID()")) return Row(("КодСотрудника", owner.ActorId));
                if (CommandText.Contains("@RoleId")) return Row(("Value", 1));
                if (CommandText.Contains("fn_ТекущиеРоли"))
                    return Row(("RoleId", (int)LocationFactData.RequiredRole), ("PersonId", 0));
                if (CommandText.Contains("AS CanConfirm"))
                    return Row(("NowUtc", new DateTime(2026, 11, 26, 14, 0, 0)), ("CanConfirm", true), ("CanMove", true));
                if (CommandText.StartsWith("INSERT dbo.vwРеальноеРасположение", StringComparison.Ordinal))
                {
                    Assert.Equal(IsolationLevel.Serializable, DbTransaction!.IsolationLevel);
                    owner.Inserts++;
                    owner.SentNotes = (string)Param("Notes")!;
                    owner.SentBroken = (int)Param("Broken")!;
                    if (owner.LoseInsertResponse) throw SqlExceptionFactory.Create(-2, "Тестовая потеря ответа INSERT");
                    return Row(("Count", 1));
                }
                if (CommandText.Contains("WITH Booked AS"))
                {
                    owner.ResponsibleOnLastList = Param("ResponsibleId") is int responsible ? responsible : null;
                    return Row(("Id", 17), ("Name", Snapshot.Equipment.FactName), ("CheckedAt", (DateTime?)null),
                        ("LocationId", 10), ("LocationName", Snapshot.Site.Name), ("Broken", false), ("Notes", ""));
                }
                if (CommandText.Contains("КодСотрудника") && CommandText.Contains("@Id"))
                    return Row(("КодСотрудника", 4), ("Сотрудник", "Проверяющий"));
                if (CommandText.Contains("AS BookedLocationName"))
                    return Row(("EquipmentId", owner.EquipmentId), ("OwnerId", 4), ("OwnerName", Snapshot.Equipment.OwnerName),
                        ("FactName", Snapshot.Equipment.FactName), ("InventoryNumber", ""), ("BookedLocationId", 10),
                        ("BookedLocationName", Snapshot.Site.Name), ("BookedFromUtc", (DateTime?)null),
                        ("EquipmentChangedAt", Snapshot.Equipment.EquipmentChangedAt));
                if (CommandText.Contains("РеальноеРасположениеОборудования")) return EmptyRows();
                if (CommandText.Contains("Расположения"))
                    return Row(("Id", 10), ("Name", Snapshot.Site.Name), ("IsWarehouse", true));
                throw new InvalidOperationException("Неожиданное чтение fixture инвентаризации.");
            }

            private object? Param(string name) => Parameters.Cast<DbParameter>().Single(p => p.ParameterName == name).Value;

            private static DbDataReader EmptyRows()
            {
                var table = new DataTable();
                table.Columns.Add("CheckedAt", typeof(DateTime));
                return table.CreateDataReader();
            }

            private static DbDataReader Row(params (string Name, object? Value)[] values)
            {
                var table = new DataTable();
                foreach (var (name, value) in values) table.Columns.Add(name, value?.GetType() ?? typeof(DateTime));
                table.Rows.Add(values.Select(value => value.Value ?? DBNull.Value).ToArray());
                return table.CreateDataReader();
            }
        }
    }
}
