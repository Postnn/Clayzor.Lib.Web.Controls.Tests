using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using static Clayzor.Lib.Web.Controls.Tests.ModelEquipmentWriteBoundaryTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет фазы записи шаблона и переходы после неопределённого результата.</summary>
public sealed class PhoneTemplateOutcomeTests
{
    private const string OriginalText = "fixture-previous-template";
    private const string ProposedText = "fixture-proposed-template";
    private const string InputScope = "template-input";
    private const string SubmissionScope = "template-submission";
    private const string WriteKind = "equipment-phone-template.save";
    private static readonly DateTime ChangedAt = new(2026, 11, 26, 8, 0, 0);

    /// <summary>Потерянный ответ долговечного Begin не допускает UPDATE.</summary>
    [Fact]
    public async Task LostBeginAcknowledgement_DoesNotWriteTemplate()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetException(new JSDisconnectedException("fixture begin lost"));
        var page = Render(context);
        await EditAsync(page, fixture, ProposedText);

        await SaveAsync(page, close: true);

        Assert.Equal(0, fixture.WriteCount);
        Assert.Contains("Запись не отправлена", page.Markup);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        var payload = Assert.IsType<string>(begin.Arguments[1]);
        Assert.DoesNotContain(OriginalText, payload);
        Assert.DoesNotContain(ProposedText, payload);
        using var intent = JsonDocument.Parse(payload);
        Assert.True(intent.RootElement.GetProperty("blockOtherDraftOperations").GetBoolean());
    }

    /// <summary>Неясный post-dispatch результат сохраняется без выдачи текста в журнал SQL или HTML.</summary>
    [Fact]
    public async Task UnknownDispatch_BlocksReplayEvenAfterReadAndSuppressesSqlDiagnostics()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер),
            Script.Error(SqlExceptionFactory.Create(53, OriginalText + " " + ProposedText)));
        var page = Render(context);
        await EditAsync(page, fixture, ProposedText);

        await SaveAsync(page);

        Assert.Contains("результат", page.Find(".mud-alert").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.WriteCount);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Empty(fixture.Errors.Messages);
        Assert.DoesNotContain(ProposedText, page.Find(".mud-alert").TextContent);
        var metadata = page.FindAll("*").SelectMany(element => element.Attributes)
            .Where(attribute => attribute.Name.StartsWith("data-", StringComparison.Ordinal) || attribute.Name == "title");
        Assert.DoesNotContain(metadata, attribute => attribute.Value.Contains(OriginalText) || attribute.Value.Contains(ProposedText));
        fixture.Enqueue(ModelRow(ProposedText));
        await Button(page, "Прочитать состояние шаблона").ClickAsync();
        await SaveAsync(page);

        Assert.Equal(1, fixture.WriteCount);
        Assert.Contains("не подтверждает результат", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
    }

    /// <summary>После открытия нового компонента Running/Unknown продолжает блокировать Save.</summary>
    [Theory]
    [InlineData("running")]
    [InlineData("unknown")]
    public async Task RestoredUnresolvedAttempt_DoesNotBeginOrDispatch(string status)
    {
        var fixture = Create(status);
        await using var context = fixture.Context;
        var page = Render(context);
        await EditAsync(page, fixture, ProposedText);

        await SaveAsync(page);

        Assert.Contains("Повтор записи заблокирован", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Доказанный CAS conflict завершает попытку без Unknown и сохраняет ввод.</summary>
    [Fact]
    public async Task CasRejection_RejectsAttemptWithoutClearingDraftText()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(0));
        var page = Render(context);
        await EditAsync(page, fixture, ProposedText);

        await SaveAsync(page);

        Assert.Equal(1, fixture.WriteCount);
        Assert.Contains("были изменены либо удалены", page.Markup);
        Assert.Equal(ProposedText, page.Find("textarea").TextContent);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }

    /// <summary>Подтверждённая запись с ошибкой очистки не повторяется ни автоматически, ни кнопкой.</summary>
    [Fact]
    public async Task ConfirmedCleanupFailure_RetriesOnlyJournalAndRead()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(1));
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ => true).SetException(new JSException("cleanup failure"));
        var page = Render(context);
        await EditAsync(page, fixture, ProposedText);

        await SaveAsync(page);
        await SaveAsync(page);

        Assert.Contains("Запись шаблона подтверждена", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Equal(1, fixture.WriteCount);

        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ => true).SetVoidResult();
        fixture.Enqueue(ModelRow(ProposedText));
        await Button(page, "Проверить журнал сохранения").ClickAsync();

        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.commitOperation"].Count);
        Assert.Equal(1, fixture.WriteCount);
    }

    /// <summary>Долговечный Confirmed в новом circuit подтверждает только очистку и чтение.</summary>
    [Fact]
    public async Task RestoredConfirmed_ClearsJournalWithoutStartingAnotherWrite()
    {
        var fixture = Create("confirmed");
        await using var context = fixture.Context;
        var page = Render(context);

        await SaveAsync(page);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Contains("Запись шаблона подтверждена", page.Markup);
        fixture.Runtime.EditorText = OriginalText;
        fixture.Enqueue(ModelRow());
        await Button(page, "Проверить журнал сохранения").ClickAsync();

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Поздний ввод остаётся в живой форме после подтверждённой записи предыдущего текста.</summary>
    [Fact]
    public async Task LateLiveInput_AfterDispatchRemainsEditableAfterReadback()
    {
        const string late = "fixture-late-local-input";
        var fixture = Create();
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(1), ModelRow(ProposedText));
        var confirmation = context.JSInterop.SetupVoid("clayDraftStore.recordConfirmedOperation", _ => true);
        var page = Render(context);
        await EditAsync(page, fixture, ProposedText);

        var save = SaveAsync(page);
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]));
        fixture.Runtime.EditorText = late;
        await EditAsync(page, fixture, late);
        confirmation.SetVoidResult();
        await save;

        Assert.Equal(1, fixture.WriteCount);
        Assert.Equal(late, page.Find("textarea").TextContent);
        Assert.False(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        var updateIndex = fixture.Connection.CommandLog.FindIndex(IsWrite);
        Assert.Equal(ProposedText, fixture.Connection.ParameterLog[updateIndex]["PhoneTemplate"]);
    }

    /// <summary>Восстановленный ввод передаёт настоящую старую версию CAS, а не свежее значение из БД.</summary>
    [Fact]
    public async Task RestoredDraft_UsesOriginalTemplateAndVersionForCas()
    {
        const string previous = "fixture-original-before-other-editor";
        const string local = "fixture-local-restored";
        var original = new EquipmentModelWriteBaseline(7, (int)ТипыОборудования.IP_телефон, ChangedAt.AddMinutes(-1), previous);
        var fixture = Create(scopes: new Dictionary<string, string>
        {
            [InputScope] = JsonSerializer.Serialize(new { Original = original, Current = local }),
        });
        fixture.Runtime.EditorText = local;
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(0));
        var page = Render(context, local);

        await SaveAsync(page);

        Assert.Equal(1, fixture.WriteCount);
        var logged = fixture.Connection.ParameterLog[fixture.Connection.CommandLog.FindIndex(IsWrite)];
        Assert.Equal(original.ChangedAt, logged["ExpectedChangedAt"]);
        Assert.Equal(previous, logged["ExpectedPhoneTemplate"]);
        Assert.Equal(local, page.Find("textarea").TextContent);
        var intent = Assert.IsType<string>(Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]).Arguments[1]);
        Assert.DoesNotContain(previous, intent);
        Assert.DoesNotContain(local, intent);
        Assert.DoesNotContain(OriginalText, intent);
    }

    /// <summary>Файл заменяет текущую защищённую область вместе со старым raw, сохраняя исходный CAS.</summary>
    [Fact]
    public async Task FileImport_ReplacesProtectedScopeAndRemountRestoresNewText()
    {
        const string imported = "fixture-imported-private-template";
        var fixture = Create();
        await using var context = fixture.Context;
        var page = Render(context);
        await EditAsync(page, fixture, "older local text");
        var file = new TemplateFile(imported);

        await page.InvokeAsync(() => (Task)typeof(PhoneTemplateForm)
            .GetMethod("LoadFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page.Instance, [new InputFileChangeEventArgs([file])])!);
        page.Render();

        Assert.Equal(imported, page.Find("textarea").TextContent);
        var replace = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal(InputScope, replace.Arguments[1]);
        Assert.Equal(25L, replace.Arguments[2]);
        var protectedPayload = Assert.IsType<string>(replace.Arguments[3]);
        using var draft = JsonDocument.Parse(protectedPayload);
        Assert.Equal(OriginalText, draft.RootElement.GetProperty("Original").GetProperty("PhoneTemplate").GetString());
        Assert.Equal(imported, draft.RootElement.GetProperty("Current").GetString());
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);

        var reopened = Create(scopes: new Dictionary<string, string> { [InputScope] = protectedPayload });
        await using var restoredContext = reopened.Context;
        var restored = Render(restoredContext, imported);
        Assert.Equal(imported, restored.Find("textarea").TextContent);
        Assert.Equal(0, reopened.WriteCount);
    }

    /// <summary>Поздний N+1 и подтверждённая отправка переживают cleanup и новый экземпляр формы.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedCleanup_RemountKeepsLateInputAndRebasesFromProtectedSubmission(bool keepTyped)
    {
        const string late = "fixture-late-after-dispatch";
        var first = Create();
        await using var context = first.Context;
        first.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(1));
        var confirmation = context.JSInterop.SetupVoid("clayDraftStore.recordConfirmedOperation", _ => true);
        context.JSInterop.SetupVoid("clayModelEquipmentForm.refreshOpener", _ => true)
            .SetException(new JSException("fixture post-commit read blocked"));
        var page = Render(context);
        await EditAsync(page, first, ProposedText);

        var saving = SaveAsync(page);
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]));
        first.Runtime.EditorText = late;
        await EditAsync(page, first, late);
        confirmation.SetVoidResult();
        await saving;

        var typedWrites = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"];
        var lateDraft = Assert.IsType<string>(typedWrites.Last(invocation => Equals(invocation.Arguments[1], InputScope)).Arguments[2]);
        var receipt = Assert.IsType<string>(typedWrites.Last(invocation => Equals(invocation.Arguments[1], SubmissionScope)).Arguments[2]);
        using var proof = JsonDocument.Parse(receipt);
        Assert.True(proof.RootElement.GetProperty("Confirmed").GetBoolean());
        Assert.Equal(ProposedText, proof.RootElement.GetProperty("Text").GetString());
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(1, first.WriteCount);

        var scopes = new Dictionary<string, string> { [SubmissionScope] = receipt };
        if (keepTyped) scopes[InputScope] = lateDraft;
        var reopened = Create(scopes: scopes, hasRaw: true, databaseText: ProposedText);
        reopened.Runtime.EditorText = late;
        await using var restoredContext = reopened.Context;
        var restored = Render(restoredContext, late);
        restored.WaitForAssertion(() => Assert.Contains("Запись шаблона подтверждена", restored.Markup));
        reopened.Enqueue(ModelRow(ProposedText));

        await Button(restored, "Проверить журнал сохранения").ClickAsync();

        Assert.Equal(0, reopened.WriteCount);
        Assert.Equal(late, restored.Find("textarea").TextContent);
        Assert.False(Button(restored, "Применить").HasAttribute("disabled"));
        var rebase = Assert.Single(restoredContext.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        using var rebased = JsonDocument.Parse(Assert.IsType<string>(rebase.Arguments[3]));
        Assert.Equal(ProposedText, rebased.RootElement.GetProperty("Original").GetProperty("PhoneTemplate").GetString());
        Assert.Equal(late, rebased.RootElement.GetProperty("Current").GetString());
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(rebase.Arguments[4]));
    }

    /// <summary>Failed области не должен теряться при пустом результате Apply.</summary>
    [Fact]
    public async Task TemplateRawReadiness_EmptyApplyStillShowsRetryForFailedScope()
    {
        var fixture = Create(hasRaw: true);
        fixture.Runtime.EditorText = OriginalText;
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [], [InputScope]));
        var page = Render(context);

        page.WaitForAssertion(() => Assert.Contains("Повторить восстановление ввода", page.Markup));
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Повтор сохраняет новый большой текст и отправляет только метаданные pending.</summary>
    [Fact]
    public async Task TemplateRawRetry_IsSilentAndPreservesNewerTemplateAndUnknown()
    {
        var fixture = Create(status: "unknown", hasRaw: true);
        fixture.Runtime.EditorText = OriginalText;
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [InputScope], []));
        var page = Render(context);
        page.WaitForAssertion(() => Assert.NotNull(Button(page, "Повторить восстановление ввода")));
        var newer = new string('Я', 256 * 1024);
        await EditAsync(page, fixture, newer);
        fixture.Enqueue(Roles(Роли.Дежурный_инженер));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, ["other-editor"], []));

        await page.InvokeAsync(() => Button(page, "Повторить восстановление ввода").ClickAsync());

        var retry = Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
        Assert.Equal(InputScope, retry.Arguments[1]);
        Assert.Equal(false, retry.Arguments[2]);
        Assert.Equal(newer, page.Find("textarea").TextContent);
        Assert.DoesNotContain("Повторить восстановление ввода", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.retryStorage"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Потеря права после открытия не отправляет ни raw, ни storage retry.</summary>
    [Fact]
    public async Task TemplateRawRetry_RevalidatesActorPermission()
    {
        var fixture = Create(hasRaw: true);
        fixture.Runtime.EditorText = OriginalText;
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [], [InputScope]));
        var page = Render(context);
        page.WaitForAssertion(() => Assert.NotNull(Button(page, "Повторить восстановление ввода")));
        fixture.Enqueue(Roles(Роли.Наблюдатель_услуг_связи));

        await page.InvokeAsync(() => Button(page, "Повторить восстановление ввода").ClickAsync());

        Assert.Contains("Не удалось подтвердить сотрудника", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Поздний callback silent восстановления не публикует поверх новой ревизии текста.</summary>
    [Fact]
    public async Task TemplateRawRetry_LateCompletionPreservesNewerEpoch()
    {
        var fixture = Create(hasRaw: true);
        fixture.Runtime.EditorText = OriginalText;
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [InputScope], []));
        var page = Render(context);
        page.WaitForAssertion(() => Assert.NotNull(Button(page, "Повторить восстановление ввода")));
        fixture.Enqueue(Roles(Роли.Дежурный_инженер));
        var apply = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true);
        var retry = page.InvokeAsync(() => Button(page, "Повторить восстановление ввода").ClickAsync());
        try
        {
            page.WaitForAssertion(() => Assert.Single(apply.Invocations));
            await EditAsync(page, fixture, "newer while restore waits");
            context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
                .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        }
        finally { apply.SetResult(new ClayDraftRawApplyResult([], [])); }
        await retry.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("newer while restore waits", page.Find("textarea").TextContent);
        Assert.Contains("Ввод изменился", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Готовность принимаемой области перед Begin даёт пользователю повтор восстановления.</summary>
    [Fact]
    public async Task TemplateWriteGate_ShowsRecoveryBeforeAnyBegin()
    {
        var fixture = Create();
        fixture.Runtime.EditorText = OriginalText;
        await using var context = fixture.Context;
        var page = Render(context);
        await EditAsync(page, fixture, ProposedText);
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(0));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [InputScope], []));

        await SaveAsync(page);

        Assert.Contains("Повторить восстановление ввода", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Защищённый снимок и DOM текст больше обычного сообщения SignalR передаются потоком.</summary>
    [Fact]
    public async Task LargeRestoredTemplate_PassesExistingCasOverStreamedRead()
    {
        var large = new string('Ж', 512 * 1024);
        var original = new EquipmentModelWriteBaseline(7, (int)ТипыОборудования.IP_телефон, ChangedAt, OriginalText);
        var fixture = Create(scopes: new Dictionary<string, string>
        {
            [InputScope] = JsonSerializer.Serialize(new { Original = original, Current = large }),
        }, hasRaw: true);
        fixture.Runtime.EditorText = large;
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(0));
        var page = Render(context, large);

        await SaveAsync(page);

        Assert.Equal(1, fixture.WriteCount);
        var args = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]).Arguments;
        Assert.True(Assert.IsType<string>(args[1]).Length < 1024);
        var raw = Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal(false, raw.Arguments[2]);
        Assert.Equal(large.Length, page.Find("textarea").TextContent.Length);
    }

    /// <summary>Ошибочный или превышающий лимит поток освобождает регистрацию и скрывает содержание.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(1024)]
    public async Task InvalidRestoreStream_ReleasesRegistrationWithoutDisclosingText(int limit)
    {
        const string hidden = "fixture-private-payload";
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var stream = new TemplateTestStream(Encoding.UTF8.GetBytes("{" + hidden));
        context.JSInterop.Setup<ClayDraftRestoreStream?>("clayDraftStore.registerStream", _ => true)
            .SetResult(new ClayDraftRestoreStream("invalid-fixture", stream));
        await using var service = new ClayDraftService(context.JSInterop.JSRuntime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterHandleAsync("template", "7", "4", 1, ".fixture", limit));

        Assert.True(stream.Disposed);
        Assert.False(service.HasActiveRegistration);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unregister"]);
        Assert.DoesNotContain(hidden, error.ToString());
    }

    /// <summary>После неверного потока восстановления выбранного черновика прежний handle блокируется.</summary>
    [Fact]
    public async Task InvalidAdoptionStream_DisposesTheOriginalHandle()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var initial = new ClayDraftRestore("fixture-id", false, 0, null, null);
        context.JSInterop.Setup<ClayDraftRestoreStream?>("clayDraftStore.registerStream", _ => true)
            .SetResult(new ClayDraftRestoreStream(initial.RegistrationId,
                new TemplateTestStream(JsonSerializer.SerializeToUtf8Bytes(initial))));
        context.JSInterop.Setup<ClayDraftRestoreStream?>("clayDraftStore.recoverListedDraftStream", _ => true)
            .SetResult(new ClayDraftRestoreStream(initial.RegistrationId,
                new TemplateTestStream(Encoding.UTF8.GetBytes("invalid protected draft"))));
        await using var service = new ClayDraftService(context.JSInterop.JSRuntime);
        var journal = await service.RegisterHandleAsync("template", "7", "4", 1, ".fixture", 1024);

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RecoverListedDraftAsync("fixture-draft"));

        Assert.False(service.HasActiveRegistration);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unregister"]);
    }

    /// <summary>Завершившийся старый поток не перезаписывает более позднюю редакторскую ревизию.</summary>
    [Fact]
    public async Task StaleEditorStream_DoesNotPublishOldText()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        var page = Render(context);
        var observer = Assert.IsType<string>(fixture.Runtime.EditorGeneration);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = new TemplateTestStream(Encoding.UTF8.GetBytes(new string('А', 120000)), release.Task);
        fixture.Runtime.EditorRevision = 1;
        fixture.Runtime.NextSnapshot = new PhoneTemplateForm.TemplateTextWire(1, older);

        var reading = page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(observer, 1));
        await older.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var latest = new string('Б', 120000);
        fixture.Runtime.EditorText = latest;
        fixture.Runtime.EditorRevision = 2;
        release.SetResult();
        await reading;

        Assert.Equal(OriginalText, page.Find("textarea").TextContent);
        Assert.True(older.Disposed);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        await page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(observer, 2));
        Assert.Equal(latest, page.Find("textarea").TextContent);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    /// <summary>Поздняя браузерная ревизия после фиксации удерживает окно открытым до поступления потока.</summary>
    [Fact]
    public async Task CloseAfterSave_DoesNotLoseALaterEditorRevision()
    {
        const string late = "fixture-late-typed-after-confirmed";
        var fixture = Create();
        await using var context = fixture.Context;
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(1), ModelRow(ProposedText));
        var cleanup = context.JSInterop.SetupVoid("clayDraftStore.discardScope",
            call => Equals(call.Arguments[1], SubmissionScope));
        var page = Render(context);
        fixture.Runtime.EditorRevision = 1;
        await page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(fixture.Runtime.EditorGeneration!, 1));

        var running = SaveAsync(page, close: true);
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.discardScope"]));
        fixture.Runtime.EditorRevision = 2;
        fixture.Runtime.EditorText = late;
        cleanup.SetVoidResult();
        await running;
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);

        await page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(fixture.Runtime.EditorGeneration!, 2));
        Assert.Equal(late, page.Find("textarea").TextContent);
        Assert.Equal(1, fixture.WriteCount);
        var latest = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]
            .Last(call => Equals(call.Arguments[1], InputScope));
        Assert.Contains(late, Assert.IsType<string>(latest.Arguments[2]));
    }

    /// <summary>Нативная ветка сохраняет семантику textarea без input/change handlers; обычная ветка по-прежнему связывается.</summary>
    [Fact]
    public async Task BrowserManagedTextarea_HasNativeSemanticsWithoutChangingDefaultBinding()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var passive = context.Render<ClayTextArea>(parameters => parameters
            .Add(x => x.Value, "fixture value")
            .Add(x => x.Options, new ClayTextAreaOptions
            {
                BrowserManaged = true, BrowserLabel = "Шаблон телефона", Class = "fixture-editor",
                DraftKey = "phone-template.text", Lines = 4, MaxLength = 20,
            }));
        var textarea = passive.Find("textarea");
        Assert.NotNull(textarea.Closest(".fixture-editor"));
        Assert.Equal("Шаблон телефона", textarea.GetAttribute("aria-label"));
        Assert.Equal("phone-template.text", textarea.GetAttribute("data-draft-key"));
        Assert.Equal("4", textarea.GetAttribute("rows"));
        Assert.Equal("20", textarea.GetAttribute("maxlength"));
        Assert.DoesNotContain(textarea.Attributes, attribute => attribute.Name.Contains("oninput") || attribute.Name.Contains("onchange"));

        string? value = null;
        var regular = context.Render<ClayTextArea>(parameters => parameters
            .Add(x => x.Options, new ClayTextAreaOptions { Label = "Обычное поле" })
            .Add(x => x.ValueChanged, text => value = text));
        await regular.Find("textarea").InputAsync("обычный ввод");
        Assert.Equal("обычный ввод", value);
    }

    /// <summary>Импорт сохраняет CRLF в SQL, пока браузер показывает тот же текст с нормализованными переводами строк.</summary>
    [Fact]
    public async Task ImportedLineEndings_SurviveBrowserNormalizationBeforeSave()
    {
        const string imported = "fixture-line-one\r\nfixture-line-two";
        var fixture = Create();
        await using var context = fixture.Context;
        var page = Render(context);
        var file = new TemplateFile(imported);
        await page.InvokeAsync(() => (Task)typeof(PhoneTemplateForm)
            .GetMethod("LoadFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page.Instance, [new InputFileChangeEventArgs([file])])!);
        page.Render();
        fixture.Runtime.EditorText = imported.Replace("\r\n", "\n", StringComparison.Ordinal);
        fixture.Enqueue(ModelRow(), Roles(Роли.Дежурный_инженер), Script.Scalar(0));

        await SaveAsync(page);

        Assert.Equal(1, fixture.WriteCount);
        var parameters = fixture.Connection.ParameterLog[fixture.Connection.CommandLog.FindIndex(IsWrite)];
        Assert.Equal(imported, parameters["PhoneTemplate"]);
    }

    private sealed class TemplateFile(string text) : IBrowserFile
    {
        private readonly byte[] _content = Encoding.UTF8.GetBytes(text);

        public string Name => "fixture.cfg";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => _content.Length;
        public string ContentType => "text/plain";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream(_content, writable: false);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<PhoneTemplateForm> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private sealed class RecordingErrors : ISqlErrorHandler
    {
        internal List<string> Messages { get; } = [];

        public void HandleSqlError(Microsoft.Data.SqlClient.SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters)
        {
            Messages.Add(exception.Message);
            Messages.AddRange(parameters.Select(parameter => parameter.Value?.ToString() ?? ""));
        }
    }

    private static IRenderedComponent<PhoneTemplateForm> Render(BunitContext context, string expected = OriginalText)
    {
        var page = context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() =>
        {
            var editor = page.Find("textarea");
            Assert.Contains(expected, editor.TextContent);
            Assert.False(editor.HasAttribute("readonly"));
        }, TimeSpan.FromSeconds(3));
        return page;
    }

    private static Task EditAsync(IRenderedComponent<PhoneTemplateForm> page, TemplateFixture fixture, string text)
    {
        fixture.Runtime.EditorText = text;
        var revision = ++fixture.Runtime.EditorRevision;
        return page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(fixture.Runtime.EditorGeneration!, revision));
    }

    private static Task SaveAsync(IRenderedComponent<PhoneTemplateForm> page, bool close = false) =>
        page.InvokeAsync(() => (Task)typeof(PhoneTemplateForm)
            .GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page.Instance, [close])!);

    private static ScriptEntry ModelRow(string text = OriginalText) => Script.Rows(Script.Row(
        ("Id", 7), ("Name", "Телефон"), ("EquipmentTypeId", (int)ТипыОборудования.IP_телефон),
        ("PhoneTemplate", text), ("ChangedAt", ChangedAt)));

    [Fact]
    public async Task TemplateAttributeCatalog_ReadsOnlyAndClearsSelectionWithoutChangingTemplate()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        var page = context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() => Assert.NotNull(fixture.Runtime.EditorGeneration));
        Assert.Contains(SQLQueries.SELECT_ТипыАтрибутовДляШаблона, fixture.Connection.CommandLog);
        Assert.Contains("FROM dbo.ТипыАтрибутовТелефонов", SQLQueries.SELECT_ТипыАтрибутовДляШаблона);
        Assert.DoesNotContain("UPDATE", SQLQueries.SELECT_ТипыАтрибутовДляШаблона);
        var picker = page.FindComponent<Clayzor.Lib.Web.Controls.Components.Select.ClaySelect<PhoneTemplateAttributeType, string>>();
        Assert.Equal("Сервер прокси", Assert.Single(picker.Instance.Items!).Description);
        await page.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync("Proxy"));
        Assert.Contains(("findTemplateAttribute", "Proxy"), fixture.Runtime.ToolCalls);
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Вставить тип аттрибута в шаблон").Click();
        Assert.Contains(("insertTemplateAttribute", "Proxy"), fixture.Runtime.ToolCalls);
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Очистить значение поле Типы аттрибутов").Click();
        Assert.Null(picker.Instance.Value);
        Assert.Equal(2, fixture.Runtime.ToolCalls.Count);
        Assert.Equal(OriginalText, page.Find("textarea").TextContent);
        Assert.Equal(0, fixture.WriteCount);
    }

    [Fact]
    public async Task TemplateAttributeInsertion_IsDisabledWithUnknownWrite()
    {
        var fixture = Create("unknown");
        await using var context = fixture.Context;
        var page = context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() => Assert.NotNull(fixture.Runtime.EditorGeneration));
        var picker = page.FindComponent<Clayzor.Lib.Web.Controls.Components.Select.ClaySelect<PhoneTemplateAttributeType, string>>();
        await page.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync("Proxy"));
        Assert.True(page.FindAll("button").Single(button => button.TextContent.Trim() == "Вставить тип аттрибута в шаблон").HasAttribute("disabled"));
        Assert.DoesNotContain(fixture.Runtime.ToolCalls, call => call.Operation == "insertTemplateAttribute");
        Assert.Equal(0, fixture.WriteCount);
    }

    [Theory]
    [InlineData("Продолжить редактирование")]
    [InlineData("Закрыть без сохранения")]
    [InlineData("Отменить мои изменения и загрузить из БД")]
    public async Task CancellationUsesExplicitChoiceAndAppliesOnlyItsOwnScope(string action)
    {
        var fixture = Create();
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() => Assert.NotNull(fixture.Runtime.EditorGeneration));
        var initialReadCount = fixture.Connection.CommandLog.Count;
        const string freshText = "fixture-fresh";
        fixture.Enqueue(Roles(Роли.Дежурный_инженер), ModelRow(freshText));
        var cancel = page.InvokeAsync(() => FindCommand(page, "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Contains("Продолжить редактирование", dialogs.Markup));
        await dialogs.InvokeAsync(() => FindCommand(dialogs, action).ClickAsync());
        await cancel;
        var close = action == "Закрыть без сохранения";
        var reset = action == "Отменить мои изменения и загрузить из БД";
        page.WaitForAssertion(() =>
        {
            Assert.Equal(reset ? freshText : OriginalText, page.Find("textarea").TextContent);
            Assert.Equal(close, (context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count > 0));
            Assert.Equal(reset || close, (context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count > 0));
        });
        if (close || reset)
        {
            var replacement = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal(InputScope, replacement.Arguments[1]);
            Assert.Equal(25L, replacement.Arguments[2]);
            if (close) Assert.Null(replacement.Arguments[3]);
            else Assert.Contains(freshText, Assert.IsType<string>(replacement.Arguments[3]));
        }
        Assert.Equal(initialReadCount + (reset ? 2 : 0), fixture.Connection.CommandLog.Count);
        Assert.Equal(0, fixture.WriteCount);
    }

    [Theory]
    [InlineData("late")]
    [InlineData("rights")]
    [InlineData("read")]
    [InlineData("storage")]
    public async Task ResetKeepsInFlightInputWhenTransitionCannotBeAccepted(string failure)
    {
        var fixture = Create();
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() => Assert.NotNull(fixture.Runtime.EditorGeneration));
        if (failure == "rights")
            fixture.Enqueue(Roles());
        else
            fixture.Enqueue(Roles(Роли.Дежурный_инженер), failure == "read"
                ? Script.Error(SqlExceptionFactory.Create(-2, "fixture read timeout"))
                : ModelRow("fixture-fresh"));
        if (failure == "storage")
            context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(false);
        var cancel = page.InvokeAsync(() => FindCommand(page, "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Contains("Продолжить редактирование", dialogs.Markup));
        if (failure == "late")
        {
            fixture.Runtime.EditorText = "fixture-N+1";
            fixture.Runtime.EditorRevision++;
        }
        else
            await page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(fixture.Runtime.EditorGeneration!, 0));
        await dialogs.InvokeAsync(() => FindCommand(dialogs, "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel;
        var expectedMessage = failure switch
        {
            "rights" => "Ввод сохранён",
            "read" => "Непринятый ввод оставлен",
            _ => "Подтвердите отмену ещё раз",
        };
        page.WaitForAssertion(() => Assert.Contains(expectedMessage, page.Markup));
        Assert.Equal(failure == "late" ? "fixture-N+1" : ProposedText, fixture.Runtime.EditorText);
        Assert.Equal(failure == "storage", (context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count > 0));
        Assert.False((context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count > 0));
        Assert.Equal(0, fixture.WriteCount);
    }

    [Fact]
    public async Task CancellationAndRefreshLeaveGenuineUnknownUntouched()
    {
        var fixture = Create("unknown");
        await using var context = fixture.Context;
        var page = context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() => Assert.NotNull(fixture.Runtime.EditorGeneration));
        var initialReadCount = fixture.Connection.CommandLog.Count;
        await page.InvokeAsync(() => FindCommand(page, "Отмена").ClickAsync());
        await page.InvokeAsync(() => FindCommand(page, "Обновить").ClickAsync());
        Assert.Contains("Ввод и попытка сохранены", page.Markup);
        Assert.False((context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count > 0));
        Assert.False((context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count > 0));
        Assert.Equal(initialReadCount, fixture.Connection.CommandLog.Count);
        Assert.Equal(0, fixture.WriteCount);
    }

    [Fact]
    public async Task CleanCancelClosesWithoutConfirmationOrDraftReplacement()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        fixture.Runtime.EditorText = OriginalText;
        var page = Render(context);
        await page.InvokeAsync(() => FindCommand(page, "Отмена").ClickAsync());
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.DoesNotContain("Продолжить редактирование", dialogs.Markup);
    }

    [Fact]
    public async Task RefreshAfterExplicitResetRejectsAnOlderStreamEvenAtTheSameBrowserRevision()
    {
        var fixture = Create();
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = Render(context);
        var editor = Assert.IsType<string>(fixture.Runtime.EditorGeneration);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = new TemplateTestStream(Encoding.UTF8.GetBytes(ProposedText), release.Task);
        fixture.Runtime.NextSnapshot = new PhoneTemplateForm.TemplateTextWire(0, older);
        var oldRead = page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(editor, 0));
        await older.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        const string fresh = "fixture-refreshed-after-reset";
        fixture.Enqueue(Roles(Роли.Дежурный_инженер), ModelRow(fresh));
        var refresh = page.InvokeAsync(() => FindCommand(page, "Обновить").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Contains("Продолжить редактирование", dialogs.Markup));
        await dialogs.InvokeAsync(() => FindCommand(dialogs, "Отменить мои изменения и загрузить из БД").ClickAsync());
        await refresh;
        page.WaitForAssertion(() => Assert.Equal(fresh, page.Find("textarea").TextContent));
        release.SetResult();
        await oldRead;

        Assert.Equal(fresh, page.Find("textarea").TextContent);
        Assert.True(older.Disposed);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    private static AngleSharp.Dom.IElement FindCommand<T>(IRenderedComponent<T> page, string text) where T : IComponent =>
        page.FindAll("button").Single(element => element.TextContent.Trim() == text);

    private static TemplateFixture Create(string? status = null, IReadOnlyDictionary<string, string>? scopes = null,
        bool hasRaw = false, string databaseText = OriginalText)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("template-fixture", status is not null || scopes is not null || hasRaw, 0, null,
                status is null ? null : OperationJson(status), scopes));
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true).SetResult([]);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true).SetResult(OperationJson("running"));
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        var runtime = new JournalFixture(context.JSInterop.JSRuntime);
        context.Services.AddSingleton<IJSRuntime>(runtime);
        context.Services.AddScoped(_ => new ClayDraftService(runtime));
        context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(11);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(25, hasRaw, status is not null));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult(["data:phone-template.text:0"], []));
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(30, []));
        context.JSInterop.Setup<bool>("clayDraftStore.rebaseScope", _ => true).SetResult(true);
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        var entries = new Queue<ScriptEntry>([ModelRow(databaseText), Roles(Роли.Дежурный_инженер),
            Script.Rows(Script.Row(("Type", "Proxy"), ("Description", "Сервер прокси")))]);
        var connection = new ScriptedConnection(entries);
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/Equipment/PhoneTemplateForm?id=7");
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton<AuthenticationStateProvider, TestAuthenticationStateProvider>();
        var errors = new RecordingErrors();
        context.Services.AddSingleton(new DbManager("Server=fixture", errors, () => connection));
        return new TemplateFixture(context, connection, entries, errors, runtime);
    }

    private static string OperationJson(string status) => $$"""
        {"version":1,"attemptId":"fixture-attempt","kind":"{{WriteKind}}","status":"{{status}}",
         "revision":9,"data":"{\"Id\":7}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}
        """;

    private sealed record TemplateFixture(BunitContext Context, ScriptedConnection Connection, Queue<ScriptEntry> Entries, RecordingErrors Errors, JournalFixture Runtime)
    {
        internal int WriteCount => Connection.CommandLog.Count(sql => sql == SQLQueries.UPDATE_МоделиОборудования_ШаблонТелефона);
        internal void Enqueue(params ScriptEntry[] entries)
        {
            foreach (var entry in entries)
                Entries.Enqueue(entry);
        }
    }

    private sealed class JournalFixture(IJSRuntime inner) : IJSRuntime, IJSObjectReference
    {
        internal string EditorText { get; set; } = ProposedText;
        internal long EditorRevision { get; set; }
        internal string? EditorGeneration { get; private set; }
        internal PhoneTemplateForm.TemplateTextWire? NextSnapshot { get; set; }
        internal List<(string Operation, string Type)> ToolCalls { get; } = [];
        private long _inputRevision;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            if (identifier == "import" && Equals(args?.FirstOrDefault(), "./js/phoneTemplateDraftText.js"))
                return (T)(object)this;
            if (identifier == "watchTemplateText")
                EditorGeneration = (string)args![2]!;
            if (identifier is "findTemplateAttribute" or "insertTemplateAttribute")
            {
                Assert.Equal(EditorGeneration, args![1]);
                ToolCalls.Add((identifier, (string)args[2]!));
                return (T)(object)true;
            }
            if (identifier == "closeTemplateIfCurrent")
            {
                if (Convert.ToInt64(args![2]) != EditorRevision)
                    return (T)(object)false;
                await inner.InvokeVoidAsync("clayModelEquipmentForm.close", token);
                return (T)(object)true;
            }
            if (identifier == "currentTemplateRevision")
                return (T)(object)EditorRevision;
            if (identifier == "freezeTemplateInput")
                return (T)(object)(Equals(EditorGeneration, args![1]) && Convert.ToInt64(args[2]) == EditorRevision);
            if (identifier == "readTemplateSnapshot")
            {
                var next = NextSnapshot ?? new PhoneTemplateForm.TemplateTextWire(EditorRevision,
                    new TemplateTestStream(Encoding.UTF8.GetBytes(EditorText)));
                NextSnapshot = null;
                return (T)(object)next;
            }

            if (identifier == "clayDraftStore.registerStream")
            {
                var restored = await inner.InvokeAsync<ClayDraftRestore?>("clayDraftStore.register", token, args);
                if (restored is null) return default!;
                return (T)(object)new ClayDraftRestoreStream(restored.RegistrationId,
                    new TemplateTestStream(JsonSerializer.SerializeToUtf8Bytes(restored)));
            }
            var result = await inner.InvokeAsync<T>(identifier, token, args);
            if (identifier == "clayDraftStore.saveTypedScope" && Equals(args?[1], InputScope) && result is long revision)
                _inputRevision = revision;
            if (identifier != "clayDraftStore.beginOperationIntent" || result is not string operationJson)
                return result;
            using var intent = JsonDocument.Parse((string)args![1]!);
            var returned = JsonNode.Parse(operationJson)!;
            returned["attemptId"] = intent.RootElement.GetProperty("attemptId").GetString();
            returned["data"] = intent.RootElement.GetProperty("data").GetString();
            returned["scopes"] = JsonSerializer.SerializeToNode(new[]
            {
                new { name = InputScope, typedRevision = _inputRevision, rawRevisions = new Dictionary<string, long>() },
            });
            return (T)(object)returned.ToJsonString();
        }
    }
}

internal sealed class TemplateTestStream(byte[] content, Task? release = null) : IJSStreamReference
{
    public long Length => content.LongLength;
    internal bool Disposed { get; private set; }
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Length > maxAllowedSize)
            throw new ArgumentOutOfRangeException(nameof(maxAllowedSize));
        Started.TrySetResult();
        if (release is not null)
            await release.WaitAsync(cancellationToken);
        return new MemoryStream(content, writable: false);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
