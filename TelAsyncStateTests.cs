using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Clayzor.Lib.Entities;
using Bunit;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Phones;
using Kesco.App.Web.Inventory.Components.Pages.Telefon;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки версионирования зависимых асинхронных операций формы Tel.</summary>
public sealed class TelAsyncStateTests
{
    /// <summary>Поздний ответ сотрудника A не перезаписывает данные сотрудника B.</summary>
    [Fact]
    public async Task Employee_LatePreviousResponseIsIgnored()
    {
        await using var fixture = new TelFixture();
        var first = fixture.Invoke("OnEmployeeChangedAsync", new Employee { Id = 1, Name = "A" });
        var second = fixture.Invoke("OnEmployeeChangedAsync", new Employee { Id = 2, Name = "B" });
        fixture.Service.Organizations[2].SetResult(20);
        await second;
        fixture.Service.Organizations[1].SetResult(10);
        await first;

        var model = fixture.Model;
        Assert.Equal(2, model.EmployeeId);
        Assert.Equal(20, model.CustomerId);
        Assert.Equal("Employee2", model.AtsDescription);
        Assert.Equal(2, fixture.Get<(int Id, string Number)?>("_defaultEmployeePhone")?.Id);
    }

    /// <summary>Ручной заказчик не перезаписывается запоздавшей организацией сотрудника.</summary>
    [Fact]
    public async Task Employee_ManualCustomerChangeWins()
    {
        await using var fixture = new TelFixture();
        var selection = fixture.Invoke("OnEmployeeChangedAsync", new Employee { Id = 2, Name = "B" });
        _ = fixture.Invoke("OnCustomerChanged", 99);
        fixture.Service.Organizations[2].SetResult(20);
        await selection;

        Assert.Equal(99, fixture.Model.CustomerId);
    }

    /// <summary>Ручное описание АТС не перезаписывается поздним автозаполнением.</summary>
    [Fact]
    public async Task AtsDescription_ManualEditWinsOverLateResponse()
    {
        await using var fixture = new TelFixture();
        fixture.Model.EmployeeId = 4;
        fixture.Service.DescriptionDelay = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = fixture.Invoke("RefreshAutomaticAtsDescriptionAsync", true);
        _ = fixture.Invoke("OnAtsDescriptionChanged", "Manual");
        fixture.Service.DescriptionDelay.SetResult("Automatic");
        await refresh;

        Assert.Equal("Manual", fixture.Model.AtsDescription);
    }

    /// <summary>Save независимо повторно вычисляет capabilities вместо ожидания устаревшего ответа.</summary>
    [Fact]
    public async Task Save_RefreshesCapabilitiesBeforeNormalization()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareWrite();
        var oldCapabilities = fixture.Service.EnqueueCapabilities();
        var currentCapabilities = fixture.Service.EnqueueCapabilities();
        var selection = fixture.Invoke("OnStationChangedAsync", new PhoneStationLookup { Id = 2, Name = "New" });
        var save = fixture.Invoke("SaveAsync", true);

        Assert.Empty(fixture.Service.SaveRequests);
        currentCapabilities.SetResult(new PhoneCapabilities { AvailablePorts = ["new"] });
        await save;
        oldCapabilities.SetResult(new PhoneCapabilities { AvailablePorts = ["old"] });
        await selection;

        var request = Assert.Single(fixture.Service.SaveRequests);
        Assert.Equal(["new"], request.AvailablePorts);
        Assert.Equal(["new"], fixture.Get<PhoneCapabilities>("_capabilities").AvailablePorts);
    }

    /// <summary>Смена Xorcom очищает старую станцию до Save и настраивает новую после него.</summary>
    [Fact]
    public async Task Xorcom_StationChangeRunsOldDeleteBeforeSaveAndNewSyncAfterSave()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Service.SavedFullNumber = "2001";

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["read", "sync:old:True", "save", "sync:new:False"], fixture.Service.Calls);
        Assert.Equal(["pre:0", "post:0"], fixture.Service.WorkflowEvents);
        Assert.Equal(1, fixture.Service.Calls.Count(call => call == "sync:old:True"));
    }

    /// <summary>Ошибка SQL после очистки старой Xorcom блокирует компенсацию.</summary>
    [Fact]
    public async Task Xorcom_SaveFailureBlocksOldStationRestore()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Service.ThrowOnSave = true;

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["read", "sync:old:True", "save"], fixture.Service.Calls);
        Assert.Contains("требует сверки", fixture.Get<string?>("_telJournalNotice"));
    }

    /// <summary>Неуспешная очистка старой Xorcom запрещает SQL Save.</summary>
    [Fact]
    public async Task Xorcom_PreSaveFailureBlocksSqlWrite()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Service.FailDeletingXorcom = true;

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["read", "sync:old:True"], fixture.Service.Calls);
        Assert.Empty(fixture.Service.SaveRequests);
        Assert.Contains("SQL не отправлен", fixture.Get<string?>("_validationMessage"));
    }

    /// <summary>Итоговый план строится после серверного перерасчёта полного номера.</summary>
    [Theory]
    [InlineData(33, "pbx")]
    [InlineData(32, "99/70")]
    public async Task Save_ServerFullNumberChangeTriggersPostSync(int stationId, string key)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareSameStation(stationId, key);
        fixture.Service.SavedFullNumber = "2001";

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["save", $"sync:{key}:False"], fixture.Service.Calls);
        Assert.Equal("2001", Assert.Single(fixture.Service.SaveRequests).Phone.FullNumber);
    }

    /// <summary>Неизменный серверный полный номер не создаёт лишнюю синхронизацию.</summary>
    [Fact]
    public async Task Save_UnchangedServerFullNumberDoesNotSync()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareSameStation(33, "pbx");

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["save"], fixture.Service.Calls);
    }

    /// <summary>Delete использует актуальные сохранённые EquipmentId, а не исходный или черновой список.</summary>
    [Fact]
    public async Task Delete_UsesFreshPersistedEquipment()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareWrite();
        fixture.Model.StationId = 0;
        fixture.Service.PersistedSnapshot = new PhoneDeleteSnapshot
        {
            Phone = fixture.Model,
            Station = new PhoneStationLookup { Id = 0 },
            Relations = new PhoneRelationsSnapshot
            {
                Equipment = [new PhoneEquipmentLink { EquipmentId = 7 }, new PhoneEquipmentLink { EquipmentId = 8 }],
            },
        };
        fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment.Add(new PhoneEquipmentLink { EquipmentId = 99 });

        await fixture.Invoke("DeleteConfirmedAsync");

        Assert.Equal(["read", "delete", "sync:7:False", "sync:8:False"], fixture.Service.Calls);
        Assert.Equal([(false, 0), (false, 1)], fixture.Service.WorkflowOrdinals);
        Assert.DoesNotContain(fixture.Service.Calls, call => call.Contains("99"));
    }

    /// <summary>Уже удалённая другим оператором запись не вызывает АТС или повторный Delete.</summary>
    [Fact]
    public async Task Delete_MissingPersistedPhoneDoesNotSync()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareWrite();
        fixture.Service.PersistedSnapshot = null;

        await fixture.Invoke("DeleteConfirmedAsync");

        Assert.Equal(["read"], fixture.Service.Calls);
        Assert.False(fixture.Get<bool>("_deleted"));
        Assert.Contains("уже удалён", fixture.Get<string?>("_validationMessage"));
    }

    /// <summary>Бизнес-валидация блокирует pre-sync до внешнего эффекта Xorcom.</summary>
    [Fact]
    public async Task Xorcom_BusinessValidationBlocksPreSync()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Service.ValidationIssues.Add(new PhoneValidationIssue("AtsPort", "Порт занят"));

        await fixture.Invoke("SaveAsync", true);

        Assert.Empty(fixture.Service.Calls);
        Assert.Empty(fixture.Service.SaveRequests);
        Assert.Contains("Порт занят", fixture.Get<string?>("_validationMessage"));
    }

    /// <summary>Delete после черновой смены A→B очищает только сохранённую станцию A до SQL DELETE.</summary>
    [Fact]
    public async Task Xorcom_DeleteRunsBeforeSqlDelete()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();

        await fixture.Invoke("DeleteConfirmedAsync");

        Assert.Equal(["read", "sync:old:True", "delete"], fixture.Service.Calls);
    }

    /// <summary>Неясный SQL Delete после очистки Xorcom блокирует восстановление номера.</summary>
    [Fact]
    public async Task Xorcom_DeleteFailureBlocksOldStationRestore()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareWrite();
        fixture.SetMember("_originalPhone", fixture.Model);
        fixture.SetMember("_originalStation", new PhoneStationLookup { Id = 1, XorcomName = "old" });
        fixture.SetMember("_selectedStation", new PhoneStationLookup { Id = 1, XorcomName = "old" });
        fixture.Service.ThrowOnDelete = true;

        await fixture.Invoke("DeleteConfirmedAsync");

        Assert.Equal(["read", "sync:old:True", "delete"], fixture.Service.Calls);
        Assert.False(fixture.Get<bool>("_deleted"));
    }

    /// <summary>Предупреждение о pending без нового черновика не сообщает о несохранённых правках.</summary>
    [Fact]
    public async Task PendingSync_CloseConfirmationExplainsSavedDataAndManualSync()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: false, pendingSync: true);

        var message = fixture.InvokeText("CloseConfirmation", "Черновик не сохранён");

        Assert.Contains("Данные уже сохранены в БД", message);
        Assert.Contains("синхронизация АТС не завершена", message);
        Assert.Contains("потребуется ручная синхронизация", message);
        Assert.DoesNotContain("Черновик не сохранён", message);
    }

    /// <summary>Без АТС pending сохраняется прежнее предупреждение о черновике.</summary>
    [Fact]
    public async Task Draft_CloseConfirmationKeepsOriginalMessage()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: false);

        Assert.Equal("Черновик не сохранён", fixture.InvokeText("CloseConfirmation", "Черновик не сохранён"));
    }

    /// <summary>Реальный Cancel различает черновик и pending; отказ не закрывает форму и не меняет данные.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancel_ConfirmationSeparatesDraftAndPending(bool draft, bool pendingSync)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft, pendingSync);
        if (!draft && !pendingSync)
        {
            await fixture.Invoke("CancelAsync").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(fixture.Get<bool>("_allowNavigation"));
            fixture.AssertNoConfirmation();
            return;
        }

        var action = fixture.Invoke("CancelAsync");
        var dialog = fixture.WaitForChild<ClayEditorCancelDialog>();
        await fixture.RespondToChildAsync(dialog, "Продолжить редактирование");
        await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(fixture.Get<bool>("_allowNavigation"));
        Assert.Equal(draft ? "draft" : string.Empty, fixture.Model.Notes);
        Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
        Assert.Empty(fixture.Service.Calls);
    }

    /// <summary>Pending без доказанной фазы блокирует Delete, отказ от обычного удаления не выполняет DML.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Delete_ToolbarConfirmationAlwaysExplainsDeletion(bool draft, bool pendingSync)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft, pendingSync);

        if (pendingSync)
        {
            await fixture.Invoke("DeleteConfirmedAsync");
            Assert.Empty(fixture.Service.Calls);
            Assert.Equal(1, fixture.PendingCount);
            return;
        }

        fixture.ClickDeleteToolbarButton();
        var message = fixture.WaitForConfirmation();
        Assert.Contains("Удалить номер, состав его PBX-группы, связи с оборудованием и параллельные вызовы?", message);
        Assert.Equal(pendingSync, message.Contains("синхронизация АТС не завершена", StringComparison.Ordinal));

        fixture.RespondToConfirmation("Нет");
        fixture.WaitForDialogClosed();

        Assert.Empty(fixture.Service.Calls);
        Assert.False(fixture.Get<bool>("_deleted"));
        Assert.False(fixture.Get<bool>("_allowNavigation"));
        Assert.Equal(draft ? "draft" : string.Empty, fixture.Model.Notes);
        Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
    }

    /// <summary>Подтверждение удаления локального черновика использует сохранённый снимок.</summary>
    [Fact]
    public async Task Delete_ToolbarConfirmedDraftDeletesPhone()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: false);

        fixture.ClickDeleteToolbarButton();
        _ = fixture.WaitForConfirmation();
        fixture.RespondToConfirmation("Да");
        fixture.WaitForDeleteCompleted();

        Assert.Equal(["read", "delete"], fixture.Service.Calls);
        Assert.True(fixture.Get<bool>("_allowNavigation"));
    }

    /// <summary>NavigationLock различает черновик и pending; отказ сохраняет URL и состояние формы.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InternalNavigation_ConfirmationSeparatesDraftAndPending(bool draft, bool pendingSync)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft, pendingSync);
        var originalUri = fixture.CurrentUri;
        if (!draft && !pendingSync)
        {
            await fixture.NavigateTo("/next").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.EndsWith("/next", fixture.CurrentUri);
            fixture.AssertNoConfirmation();
            return;
        }

        var navigation = fixture.NavigateTo("/next");
        var message = fixture.WaitForConfirmation();
        Assert.Equal(draft, message.Contains("Покинуть форму без сохранения изменений?", StringComparison.Ordinal));
        Assert.Equal(pendingSync, message.Contains("синхронизация АТС не завершена", StringComparison.Ordinal));

        fixture.RespondToConfirmation("Нет");
        await navigation.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.WaitForDialogClosed();

        Assert.Equal(originalUri, fixture.CurrentUri);
        Assert.False(fixture.Get<bool>("_allowNavigation"));
        Assert.Equal(draft ? "draft" : string.Empty, fixture.Model.Notes);
        Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
        Assert.Empty(fixture.Service.Calls);
    }

    /// <summary>Подтверждённый переход выполняется после удаления точной RAM-ревизии.</summary>
    [Fact]
    public async Task InternalNavigation_ConfirmedDraftAndPendingNavigates()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: true);

        var navigation = fixture.NavigateTo("/next");
        _ = fixture.WaitForConfirmation();
        fixture.RespondToConfirmation("Да");
        await navigation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.EndsWith("/next", fixture.CurrentUri);
    }

    /// <summary>Refresh читает актуальное состояние, сохраняя текущий ввод и фазы записи.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Refresh_PreservesDraftAndPending(bool draft, bool pendingSync)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft, pendingSync);
        var previousNumber = fixture.Model.Number;

        await fixture.Invoke("RefreshAsync").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, fixture.Service.LoadCalls);
        Assert.Equal(!draft && !pendingSync ? "reloaded" : previousNumber, fixture.Model.Number);
        Assert.Equal(draft ? "draft" : string.Empty, fixture.Model.Notes);
        Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        fixture.AssertNoConfirmation();
    }

    /// <summary>Сброс принимает свежую исходную версию и очищает только значение родительской области.</summary>
    [Fact]
    public async Task CancelReset_ReplacesExactParentRevisionWithFreshVersion()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: false);
        fixture.Service.LoadData = (id, _) => TelDraftPhone(id, changed: true);
        fixture.Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);

        var cancel = fixture.Invoke("CancelAsync");
        var dialog = fixture.WaitForChild<ClayEditorCancelDialog>();
        Assert.Equal("draft", fixture.Model.Notes);
        await fixture.RespondToChildAsync(dialog, "Отменить мои изменения и загрузить из БД");
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("New server note", fixture.Model.Notes);
        Assert.Equal(new DateTime(2026, 1, 3), fixture.Get<Phone>("_originalPhone").ChangedAtUtc);
        Assert.Equal(72, Assert.Single(fixture.Get<PhoneRelationsSnapshot>("_originalRelations").GroupMembers).PhoneId);
        var replace = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal("tel-input", replace.Arguments[1]);
        Assert.Equal(11L, replace.Arguments[2]);
        Assert.Contains("New server note", Assert.IsType<string>(replace.Arguments[3]));
        Assert.False(fixture.Get<bool>("_allowNavigation"));
    }

    /// <summary>Пропавшая версия, другой сотрудник и новая raw-ревизия сохраняют ввод.</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("actor")]
    [InlineData("read")]
    [InlineData("late")]
    public async Task CancelReset_FailurePreservesExistingDraft(string cause)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: false);
        var original = fixture.Get<Phone>("_originalPhone");
        if (cause == "version")
            fixture.Service.LoadData = (id, _) => new PhoneFormData { Phone = new Phone { Id = id }, CanEdit = true };
        if (cause == "read")
            fixture.Service.LoadData = (_, _) => throw new InvalidOperationException("fixture read failed");
        if (cause == "actor")
            fixture.Service.CurrentActor = () => new Employee { Id = 8 };
        fixture.Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(false);

        var cancel = fixture.Invoke("CancelAsync");
        var dialog = fixture.WaitForChild<ClayEditorCancelDialog>();
        await fixture.RespondToChildAsync(dialog, "Отменить мои изменения и загрузить из БД");
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Same(original, fixture.Get<Phone>("_originalPhone"));
        Assert.Equal("draft", fixture.Model.Notes);
        Assert.False(fixture.Get<bool>("_allowNavigation"));
        var calls = fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        if (cause == "late")
        {
            var replace = Assert.Single(calls);
            Assert.Equal(11L, replace.Arguments[2]);
        }
        else
            Assert.Empty(calls);
    }

    /// <summary>Ввод после начала exact CAS остаётся на форме и в родительском черновике.</summary>
    [Fact]
    public async Task CancelReset_LateTypedInputSurvivesKnownReplacement()
    {
        await using var fixture = new TelFixture();
        fixture.Service.LoadData = (id, _) => TelDraftPhone(id);
        await fixture.LoadRoute(1002, 0);
        await fixture.EditNotesAsync("draft");
        var original = fixture.Get<Phone>("_originalPhone");
        var replace = fixture.Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true);
        var cancel = fixture.Invoke("CancelAsync");
        var dialog = fixture.WaitForChild<ClayEditorCancelDialog>();
        await fixture.RespondToChildAsync(dialog, "Отменить мои изменения и загрузить из БД");
        fixture.WaitForEditorStoreReplace();

        await fixture.EditNotesAsync("late typed");
        replace.SetResult(true);
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Same(original, fixture.Get<Phone>("_originalPhone"));
        Assert.Equal("late typed", fixture.Model.Notes);
        Assert.False(fixture.Get<bool>("_allowNavigation"));
        Assert.Contains(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "tel-input") && call.Arguments[2] is string json && json.Contains("late typed"));
    }

    /// <summary>Новый номер сбрасывается в defaults на своём маршруте.</summary>
    [Fact]
    public async Task CancelReset_NewPhoneUsesCreationDefaults()
    {
        await using var fixture = new TelFixture();
        await fixture.LoadRoute(0, 0);
        await fixture.EditNotesAsync("typed on new phone");
        fixture.Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);

        var cancel = fixture.Invoke("CancelAsync");
        var dialog = fixture.WaitForChild<ClayEditorCancelDialog>();
        await fixture.RespondToChildAsync(dialog, "Отменить мои изменения и загрузить из БД");
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, fixture.Model.Id);
        Assert.Empty(fixture.Model.Notes);
        Assert.Equal(0, fixture.Service.LoadCalls);
        Assert.False(fixture.Get<bool>("_allowNavigation"));
    }

    /// <summary>Явное закрытие отменяет точную ревизию родителя.</summary>
    [Fact]
    public async Task CancelClose_PhysicallyClosesTheAcceptedRamRevision()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: false);

        var cancel = fixture.Invoke("CancelAsync");
        var dialog = fixture.WaitForChild<ClayEditorCancelDialog>();
        await fixture.RespondToChildAsync(dialog, "Закрыть без сохранения");
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(fixture.Get<bool>("_allowNavigation"));
        var close = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Equal("tel-fixture", close.Arguments[0]);
        Assert.Equal(11L, close.Arguments[1]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.discard"]);
    }

    /// <summary>После подтверждения выхода новая ревизия оставляет номер и ввод открытыми.</summary>
    [Fact]
    public async Task TelCloseWithLateInputKeepsTheRamOwner()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: false);
        fixture.Context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(false);

        var cancel = fixture.Invoke("CancelAsync");
        var dialog = fixture.WaitForChild<ClayEditorCancelDialog>();
        await fixture.RespondToChildAsync(dialog, "Закрыть без сохранения");
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(fixture.Get<bool>("_allowNavigation"));
        Assert.Equal("draft", fixture.Model.Notes);
        Assert.Contains("Ввод изменился", fixture.Get<string>("_telInputNotice"));
        Assert.Equal(11L, Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.closeRam"]).Arguments[1]);
    }



    /// <summary>При открытии без права редактирования старый ввод даже не запрашивается.</summary>
    [Fact]
    public async Task ReadOnlyTelReceivesDatabaseViewWithoutOpeningDraft()
    {
        await using var fixture = new TelFixture();
        fixture.SetupEditorRestore(DraftJson(), hasRaw: true, rawPending: false);
        fixture.Service.CurrentActor = () => new Employee { Id = 4 };
        var interop = fixture.Context.JSInterop;
        var initialRegistrations = interop.Invocations["clayDraftStore.registerRam"].Count;

        await fixture.LoadRoute(1002, 0);

        Assert.False(fixture.Get<bool>("_canEdit"));
        Assert.DoesNotContain("Restored note", fixture.Model.Notes);
        Assert.Equal(initialRegistrations, interop.Invocations["clayDraftStore.registerRam"].Count);
        Assert.Empty(interop.Invocations["clayDraftStore.applyRaw"]);
    }

    /// <summary>Результат Finder подтверждается только после создания одной связи в фактической форме Tel.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(39)]
    public async Task FinderRetriesMissingCandidateAndAcknowledgesExactRelation(int? connectionId)
    {
        Clayzor.Lib.Entities.MedicalTests.DapperColumnMapper.Initialize();
        var script = new Queue<ScriptEntry>([
            Script.Rows(),
            Script.Rows(Script.Row(("EquipmentId", 17), ("ConnectionEquipmentId", connectionId), ("Model", "Телефон")))
        ]);
        await using var fixture = new TelFixture(script);
        fixture.PrepareWrite();
        var phoneType = (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Аналоговый;
        fixture.Model.PhoneTypeId = phoneType;
        fixture.SetMember("_availability", PhoneFormRules.Evaluate(new PhoneFormRuleInput { PhoneTypeId = phoneType }));
        var search = fixture.Invoke("OpenEquipmentFinderAsync", new ClaySelectAdvancedSearchContext());
        var requestId = fixture.WaitForFinderRequest();
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { equipmentId = 17, connectionEquipmentId = connectionId });
        try
        {
            Assert.Equal("Rejected", await fixture.Finder.ApplyAsync(requestId, 1, payload));
            Assert.Empty(fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment);
            Assert.False(search.IsCompleted);
            Assert.Equal("Accepted", await fixture.Finder.ApplyAsync(requestId, 2, payload));
            Assert.Equal("Accepted", await fixture.Finder.ApplyAsync(requestId, 2, payload));
            var relation = Assert.Single(fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment);
            Assert.Equal(17, relation.EquipmentId);
            Assert.Equal(connectionId, relation.ConnectionEquipmentId);
            Assert.Empty(script);
        }
        finally
        {
            await fixture.Finder.CancelAsync(requestId);
            await search;
        }
    }

    /// <summary>Запоздавшая загрузка справочника A не меняет уже загруженную карточку B.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialLookup_LateTypeDoesNotOverwriteNewRoute(bool fromCopy)
    {
        Clayzor.Lib.Entities.MedicalTests.DapperColumnMapper.Initialize();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookups = new Queue<ScriptEntry>([
            new ScriptEntry
            {
                Started = started, Release = release,
                Rows = [Script.Row(("Id", 42), ("Name", "Тип A"))],
            },
        ]);
        await using var fixture = new TelFixture(lookupScript: lookups);
        fixture.PrepareCloseState(draft: false, pendingSync: false);
        fixture.Service.LoadData = (id, copy) => new PhoneFormData
        {
            Phone = new Phone
            {
                Id = copy ? 0 : id, Number = id == 1002 ? "1111" : "2222",
                PhoneTypeId = id == 1002 ? 42 : null,
            },
            CanEdit = true,
        };
        var loadA = fixture.LoadRoute(fromCopy ? 0 : 1002, fromCopy ? 1002 : 0);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.LoadRoute(1003, 0).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1003, fixture.Model.Id);
            Assert.Null(fixture.Get<PhoneTypeLookup?>("_selectedType"));
        }
        finally
        {
            release.TrySetResult();
            await loadA.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Equal(1003, fixture.Model.Id);
        Assert.Equal("2222", fixture.Model.Number);
        Assert.Null(fixture.Get<PhoneTypeLookup?>("_selectedType"));
        Assert.Null(fixture.Get<PhoneStationLookup?>("_originalStation"));
        Assert.Equal("2222", fixture.Get<Phone>("_originalPhone").Number);
        Assert.Empty(lookups);
        Assert.Equal(["load", "load"], fixture.Service.Calls);
    }

    /// <summary>Поздние возможности станции A не заменяют список портов карточки B.</summary>
    [Fact]
    public async Task InitialCapabilities_LateResponseDoesNotOverwriteNewRoute()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: false, pendingSync: false);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<PhoneCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new PhoneCapabilities { AvailablePorts = ["новый порт"] };
        fixture.Service.InitialCapabilities = phoneId =>
        {
            if (phoneId != 1002)
                return Task.FromResult(fresh);
            requested.TrySetResult();
            return delayed.Task;
        };
        var loadA = fixture.LoadRoute(1002, 0);
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.LoadRoute(1003, 0).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            delayed.TrySetResult(new PhoneCapabilities { AvailablePorts = ["старый порт"] });
            await loadA.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Equal(1003, fixture.Model.Id);
        Assert.Same(fresh, fixture.Get<PhoneCapabilities>("_capabilities"));
        Assert.Equal(["load", "load"], fixture.Service.Calls);
    }

    /// <summary>Срок возврата и его минимальная дата меняются вместе на границе UTC-дня.</summary>
    [Fact]
    public async Task ReturnDate_UsesOneUtcDayAndNextEditCanRefreshIt()
    {
        var clock = new TelTestClock(new DateTimeOffset(2031, 1, 7, 23, 59, 59, TimeSpan.Zero));
        await using var fixture = new TelFixture(clock: clock);
        fixture.PrepareWrite();
        var previousDay = new DateTime(2031, 1, 7);

        await fixture.InvokeEvent("OnHasReturnDateChanged", true);
        Assert.Equal(previousDay, fixture.Model.ReturnDate);
        Assert.Equal(DateTimeKind.Unspecified, fixture.Model.ReturnDate!.Value.Kind);
        Assert.Equal(previousDay, fixture.Get<ClayDatePickerOptions>("_returnDateOptions").MinDate);

        var link = new PhoneEquipmentLink { ReturnDate = previousDay };
        Assert.Null(fixture.InvokeText("EquipmentReturnRowClass", link));
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Null(fixture.InvokeText("EquipmentReturnRowClass", link));
        await fixture.InvokeEvent("OnHasReturnDateChanged", false);
        Assert.Null(fixture.Model.ReturnDate);
        await fixture.InvokeEvent("OnHasReturnDateChanged", true);
        Assert.Equal(previousDay.AddDays(1), fixture.Model.ReturnDate);
        Assert.Equal(previousDay.AddDays(1), fixture.Get<ClayDatePickerOptions>("_returnDateOptions").MinDate);
        Assert.Equal("equipment-overdue", fixture.InvokeText("EquipmentReturnRowClass", link));
        Assert.Null(fixture.InvokeText("EquipmentReturnRowClass", new PhoneEquipmentLink()));
    }

    /// <summary>Сохранение, начатое до полуночи, не меняет дату валидации при позднем ответе станции.</summary>
    [Fact]
    public async Task Save_UsesBusinessDayCapturedBeforeAsyncRollover()
    {
        var clock = new TelTestClock(new DateTimeOffset(2031, 1, 7, 23, 59, 59, TimeSpan.Zero));
        await using var fixture = new TelFixture(clock: clock);
        fixture.PrepareSameStation(33, "pbx");
        var previousDay = new DateTime(2031, 1, 7);
        fixture.Model.ReturnDate = previousDay;
        fixture.Model.Notes = "local";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PhoneCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.InitialCapabilities = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        var save = fixture.Invoke("SaveAsync", true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Now = clock.Now.AddSeconds(2);
        }
        finally
        {
            release.TrySetResult(new PhoneCapabilities());
        }
        await save.WaitAsync(TimeSpan.FromSeconds(3));
        var request = Assert.Single(fixture.Service.SaveRequests);
        Assert.Equal(previousDay, request.Today);
        Assert.Equal(DateTimeKind.Unspecified, request.Today.Kind);
        Assert.Equal(previousDay, fixture.Get<ClayDatePickerOptions>("_returnDateOptions").MinDate);
        Assert.Equal(previousDay, request.Phone.ReturnDate);
    }

    /// <summary>После доказанного CAS-конфликта Tel оставляет ввод и не запускает новую команду АТС.</summary>
    [Fact]
    public async Task CompareAndSwapConflict_DoesNotDispatchPostOrCompensatingAts()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Model.Notes = "Local notes";
        fixture.Service.HasWriteConflict = true;

        await fixture.Invoke("SaveAsync", true);

        await fixture.Invoke("SaveAsync", false);
        await fixture.Invoke("DeleteConfirmedAsync");
        Assert.Equal(["read", "sync:old:True", "save"], fixture.Service.Calls);
        Assert.Contains("Конфликт версий", fixture.Get<string?>("_validationMessage"));
        Assert.Contains("Предварительная синхронизация", fixture.Get<string?>("_warningMessage"));
        Assert.Equal("Local notes", fixture.Model.Notes);
        Assert.Equal(new DateTime(2026, 1, 2), Assert.Single(fixture.Service.SaveRequests).OriginalPhone.ChangedAtUtc);
    }

    /// <summary>Неизвестный ответ SQL после pre-cleanup не разрешает автоматическую компенсацию.</summary>
    [Fact]
    public async Task SqlUnknownAfterPreCleanup_NeverCompensates()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Service.ThrowOnSave = true;

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["read", "sync:old:True", "save"], fixture.Service.Calls);
    }

    private sealed class TelTestClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Реальная форма передаёт проверенной границе сотрудника во всех предзаписных и повторных путях.</summary>
    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("retry")]
    public async Task AtsAuthorizationDenied_DoesNotDispatchOrContinueMutation(string path)
    {
        await using var fixture = new TelFixture();
        if (path == "retry")
        {
            fixture.PrepareCloseState(draft: false, pendingSync: false);
            await fixture.RestoreWriteJournalAsync("SqlConfirmed", sqlConfirmed: true,
                new PhoneSyncOperation(PhoneSyncKind.Xorcom, "old"));
        }
        else
            fixture.PrepareXorcomStationChange();
        fixture.Service.DenyAts = true;

        if (path == "delete")
            await fixture.Invoke("DeleteConfirmedAsync");
        else if (path == "retry")
            await fixture.Invoke("RetryJournaledAtsAsync");
        else
            await fixture.Invoke("SaveAsync", true);

        Assert.Equal((@"DOMAIN\fixture", fixture.Service.Actor.Id), Assert.Single(fixture.Service.AtsActors));
        Assert.Equal(path == "retry" ? [] : ["read"], fixture.Service.Calls);
        Assert.Empty(fixture.Service.SaveRequests);
        Assert.Equal(path == "retry" ? 1 : 0, fixture.PendingCount);
        Assert.Contains("Авторизация или цель АТС не подтверждены", fixture.Get<string?>("_validationMessage"));
    }

    /// <summary>Повторная загрузка не применяет уже обработанный снимок регистрации.</summary>
    [Fact]
    public async Task TelDraftReload_DoesNotReapplyConsumedRestore()
    {
        await using var fixture = new TelFixture();
        fixture.SetupEditorRestore(DraftJson(), hasRaw: false, rawPending: false);
        fixture.Service.LoadData = (id, _) => TelDraftPhone(id);
        await fixture.LoadRoute(1002, 0);
        Assert.Equal("Restored note", fixture.Model.Notes);
        fixture.Service.LoadData = (id, _) => TelDraftPhone(id, changed: true);

        await fixture.Invoke("LoadCurrentAsync", 1002, 0, false);

        Assert.Equal("New server note", fixture.Model.Notes);
        Assert.False(fixture.Get<bool>("_telInputConflict"));
    }

    /// <summary>Кнопка повторяет только pending поля и сохраняет уже введённое примечание.</summary>
    [Fact]
    public async Task TelRawRetry_UsesOnlyPendingAndRetainsNewerInput()
    {
        await using var fixture = new TelFixture();
        fixture.SetupEditorRestore(DraftJson(), hasRaw: true, rawPending: true);
        fixture.Service.LoadData = (id, _) => TelDraftPhone(id);
        await fixture.LoadRoute(1002, 0);
        fixture.WaitForRawRetry();
        var firstApplies = fixture.Context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count;
        await fixture.EditNotesAsync("newer note");
        fixture.Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        fixture.Context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, ["tel-equipment-7-14"], []));
        await fixture.ClickRawRetryAsync();

        Assert.False(fixture.Get<bool>("_telRawPending"));
        Assert.Equal("newer note", fixture.Model.Notes);
        Assert.Equal("tel-input", Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]).Arguments[1]);
        Assert.Equal(firstApplies, fixture.Context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count);
    }

    /// <summary>Другой сотрудник не может применить сохранённый ввод при обновлении формы.</summary>
    [Fact]
    public async Task TelRawRetry_WhenActorChangedDoesNotApplyPending()
    {
        await using var fixture = new TelFixture();
        fixture.SetupEditorRestore(DraftJson(), hasRaw: true, rawPending: true);
        fixture.Service.LoadData = (id, _) => TelDraftPhone(id);
        await fixture.LoadRoute(1002, 0);
        fixture.WaitForRawRetry();
        fixture.Service.CurrentActor = () => new Employee { Id = 99 };

        await fixture.ClickRawRetryAsync();

        Assert.True(fixture.Get<bool>("_telRawPending"));
        Assert.Contains("Не удалось подтвердить сотрудника", fixture.Get<string>("_telInputNotice"));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
    }

    /// <summary>Проверка сотрудника старого маршрута не запускает восстановление в новой форме.</summary>
    [Fact]
    public async Task TelRawRetry_LateActorCannotRestoreIntoNextRoute()
    {
        await using var fixture = new TelFixture();
        fixture.SetupEditorRestore(DraftJson(), hasRaw: true, rawPending: true);
        fixture.Service.LoadData = (id, _) => TelDraftPhone(id);
        await fixture.LoadRoute(1002, 0);
        fixture.WaitForRawRetry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Employee?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.CurrentActorLookup = () =>
        {
            started.TrySetResult();
            return release.Task;
        };
        var retry = fixture.ClickRawRetryAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Service.CurrentActorLookup = null;
            fixture.SetupEmptyEditorRegistration();
            await fixture.LoadRoute(1003, 0);
        }
        finally { release.TrySetResult(fixture.Service.Actor); }
        await retry.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1003, fixture.Model.Id);
        Assert.Equal("Base", fixture.Model.Notes);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
    }

    /// <summary>Опоздавший click восстановления не переключается на повтор сохранения.</summary>
    [Fact]
    public async Task TelRawRetry_WhenRawAlreadyResolvedDoesNotRetryStorage()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareSameStation(33, "pbx");
        fixture.SetMember("_telInputSaveFailed", true);

        await fixture.Invoke("RetryTelRawAsync");

        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
    }

    /// <summary>Новый timestamp подтверждённой строки становится baseline при применении без позднего ввода.</summary>
    [Fact]
    public async Task TelDraftApply_UsesFreshTimestampAfterCommitAndFinishesCleanup()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareSameStation(33, "pbx");
        fixture.Model.AtsDescription = "Submitted";
        var expectedVersion = fixture.Model.ChangedAtUtc.AddMinutes(1);
        fixture.Service.LoadData = (id, _) => new PhoneFormData
        {
            Phone = new Phone { Id = id, Number = "1001", AtsDescription = "Submitted", ChangedAtUtc = expectedVersion },
            CanEdit = true,
        };

        await fixture.Invoke("SaveAsync", false);

        Assert.Equal(expectedVersion, fixture.Get<Phone>("_originalPhone").ChangedAtUtc);
        Assert.Null(fixture.Get<object?>("_telProgress"));
        Assert.Single(fixture.Service.SaveRequests);
    }

    /// <summary>Поздний typed ввод сохраняется после неизменяемой SQL-команды и удерживает форму.</summary>
    [Fact]
    public async Task TelTypedInputAfterDispatch_IsPersistedAndDoesNotClose()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareSameStation(33, "pbx");
        fixture.Model.AtsDescription = "Edited before dispatch";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.BeforeSaveReply = async () =>
        {
            started.TrySetResult();
            await release.Task;
        };

        var saving = fixture.Invoke("SaveAsync", true);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.EditNotesAsync("Typed later");
        }
        finally { release.TrySetResult(); }
        await saving.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(string.Empty, Assert.Single(fixture.Service.SaveRequests).Phone.Notes);
        Assert.Equal("Typed later", fixture.Model.Notes);
        Assert.Contains(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "tel-input") && call.Arguments[2] is string json &&
                json.Contains("Typed later", StringComparison.Ordinal));
        Assert.Contains("Поздний ввод", fixture.Get<string?>("_telJournalNotice"));
        Assert.False(fixture.Get<bool>("_allowNavigation"));
    }

    /// <summary>Сохранённые поля старой связи не заменяют ее текущий снимок.</summary>
    [Fact]
    public async Task TelChildStaleEquipmentBaseline_DisablesAcceptance()
    {
        await using var fixture = new TelFixture();
        fixture.SetupChildren(new Dictionary<string, string>
        {
            ["tel-equipment-7-14"] = ChildJson(new EquipmentPhoneReturnResult(null, "Old baseline"),
                new EquipmentPhoneReturnResult(null, "Child draft")),
        });
        fixture.Service.LoadData = (id, _) => TelWithEquipment(id);
        await fixture.LoadRoute(1002, 0);

        var dialog = fixture.Invoke("EditEquipmentReturnAsync", Assert.Single(fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment));
        var editor = fixture.WaitForChild<EquipmentPhoneDialog>();
        Assert.Equal("Child draft", editor.Instance.Notes);
        Assert.DoesNotContain(editor.FindAll("button"), button => button.TextContent.Trim() == "Повторить восстановление ввода");
        Assert.True(editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        await fixture.CloseDirtyChildAsync(editor);
        await dialog.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Base link", Assert.Single(fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment).Notes);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "tel-input"));
        Assert.Empty(fixture.Service.SaveRequests);
    }

    /// <summary>Поздняя raw-ревизия сохраняет открытый редактор после приёма родительского снимка.</summary>
    [Fact]
    public async Task TelChildLateRevision_NoScopeCleanupAndNoBusinessDispatch()
    {
        await using var fixture = new TelFixture();
        const string scope = "tel-equipment-7-14";
        fixture.SetupChildren(new Dictionary<string, string>
        {
            [scope] = ChildJson(new EquipmentPhoneReturnResult(null, "Base link"), new EquipmentPhoneReturnResult(null, "Accepted link")),
        });
        var interop = fixture.Context.JSInterop;
        interop.Setup<bool>("clayDraftStore.replaceEditorScope", call => Equals(call.Arguments[2], 11L)).SetResult(false);
        fixture.Service.LoadData = (id, _) => TelWithEquipment(id);
        await fixture.LoadRoute(1002, 0);

        var dialog = fixture.Invoke("EditEquipmentReturnAsync", Assert.Single(fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment));
        var editor = fixture.WaitForChild<EquipmentPhoneDialog>();
        await fixture.RespondToChildAsync(editor, "OK");
        Assert.False(dialog.IsCompleted);
        Assert.Contains("Черновик изменился", editor.Markup);
        Assert.Equal("Accepted link", Assert.Single(fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment).Notes);
        Assert.Empty(fixture.Service.SaveRequests);

        interop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", call => Equals(scope, call.Arguments[1]))
            .SetResult(new ClayDraftEditorScopeState(12, true));
        await fixture.CloseDirtyChildAsync(editor);
        await dialog.WaitAsync(TimeSpan.FromSeconds(3));

        var calls = interop.Invocations["clayDraftStore.replaceEditorScope"];
        Assert.Equal([11L, 12L], calls.Select(call => (long)call.Arguments[2]!).ToArray());
        Assert.All(calls, call => Assert.Equal(scope, call.Arguments[1]));
        Assert.Equal("Accepted link", Assert.Single(fixture.Get<PhoneRelationsSnapshot>("_relations").Equipment).Notes);
    }

    /// <summary>Удалённый после открытия составной ключ блокирует перенос черновика.</summary>
    [Fact]
    public async Task TelChildMissingEquipmentKey_DoesNotPublishToParent()
    {
        await using var fixture = new TelFixture();
        const string scope = "tel-equipment-7-14";
        fixture.SetupChildren(new Dictionary<string, string>
        {
            [scope] = ChildJson(new EquipmentPhoneReturnResult(null, "Base link"), new EquipmentPhoneReturnResult(null, "Child input")),
        });
        fixture.Service.LoadData = (id, _) => TelWithEquipment(id);
        await fixture.LoadRoute(1002, 0);
        var relations = fixture.Get<PhoneRelationsSnapshot>("_relations");

        var dialog = fixture.Invoke("EditEquipmentReturnAsync", Assert.Single(relations.Equipment));
        var editor = fixture.WaitForChild<EquipmentPhoneDialog>();
        await editor.InvokeAsync(() => relations.Equipment.Clear());
        await fixture.RespondToChildAsync(editor, "OK");

        Assert.False(dialog.IsCompleted);
        Assert.Contains("Не удалось принять черновик связи", editor.Markup);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "tel-input"));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        await fixture.CloseDirtyChildAsync(editor);
        await dialog.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(fixture.Service.SaveRequests);
    }


    /// <summary>Сброс дочерней связи требует совпадения с принятой версией.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TelChildReset_ChecksFreshEquipmentLink(bool changed)
    {
        await using var fixture = new TelFixture();
        const string scope = "tel-equipment-7-14";
        fixture.SetupChildren(new Dictionary<string, string>
        {
            [scope] = ChildJson(new EquipmentPhoneReturnResult(null, "Base link"),
                new EquipmentPhoneReturnResult(null, "Child input")),
        });
        fixture.Service.LoadData = (id, _) => TelWithEquipment(id);
        await fixture.LoadRoute(1002, 0);

        var relations = fixture.Get<PhoneRelationsSnapshot>("_relations");
        var dialog = fixture.Invoke("EditEquipmentReturnAsync", Assert.Single(relations.Equipment));
        var editor = fixture.WaitForChild<EquipmentPhoneDialog>();
        Assert.Single(editor.FindAll("[data-clay-field-changed]"));
        if (changed)
            fixture.Service.LoadData = (id, _) =>
            {
                var fresh = TelWithEquipment(id);
                fresh.Relations.Equipment.Clear();
                return fresh;
            };

        var cancel = fixture.RespondToChildAsync(editor, "Отмена");
        await fixture.RespondToChildAsync(fixture.WaitForChild<ClayEditorCancelDialog>(),
            "Отменить мои изменения и загрузить из БД");
        await cancel.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(dialog.IsCompleted);
        Assert.Equal(changed ? "Child input" : "Base link", editor.Instance.Notes);
        Assert.Equal(changed ? 1 : 0, editor.FindAll("[data-clay-field-changed]").Count);
        Assert.Equal("Base link", Assert.Single(relations.Equipment).Notes);
        Assert.Equal(changed ? 0 : 1, fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        Assert.Empty(fixture.Service.SaveRequests);
    }

    private static string ChildJson<T>(T original, T current) =>
        System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Original = original, Current = current });

    private static PhoneFormData TelWithEquipment(int id)
    {
        var data = TelDraftPhone(id);
        data.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 14, Notes = "Base link" });
        return data;
    }


    private static PhoneFormData TelDraftPhone(int id, bool changed = false) => new()
    {
        Phone = new Phone { Id = id, Number = "101", Notes = changed ? "New server note" : "Base",
            ChangedAtUtc = changed ? new DateTime(2026, 1, 3) : new DateTime(2026, 1, 2) },
        Relations = new PhoneRelationsSnapshot { GroupMembers = [new PhoneGroupMember { PhoneId = changed ? 72 : 71 }] },
        CanEdit = true,
    };

    private static string DraftJson()
    {
        var original = TelDraftPhone(1002).Phone;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1, Id = 1002, CopyId = 0, Original = original,
            OriginalRelations = new { Group = new[] { 71 }, Parallel = Array.Empty<object>(), Equipment = Array.Empty<object>() },
            Current = new Phone { Id = 1002, Number = "101", Notes = "Restored note", ChangedAtUtc = original.ChangedAtUtc },
            CurrentRelations = new
            {
                Group = new[] { 83 },
                Parallel = new[] { new { CallNumber = "111", Before = 2, Quick = 3, Disabled = false } },
                Equipment = new[] { new { EquipmentId = 7, ConnectionEquipmentId = 14, LocationId = 9, PhoneTypeId = 1, Notes = "Local link" } },
            },
            HasReturnDate = false, QuickParallel = "112",
        });
    }

    private sealed class TelFixture : IAsyncDisposable
    {
        private readonly BunitContext _context;
        private readonly IRenderedComponent<MudBlazor.MudDialogProvider> _dialogProvider;
        private readonly IRenderedComponent<Tel> _page;
        private readonly Tel _tel;

        internal TelFixture(Queue<ScriptEntry>? databaseScript = null, Queue<ScriptEntry>? lookupScript = null, TimeProvider? clock = null)
        {
            var db = new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => new ScriptedConnection(lookupScript ?? new Queue<ScriptEntry>()));
            Service = new DelayedPhoneFormService(db);
            _context = new BunitContext();
            _context.JSInterop.Mode = JSRuntimeMode.Loose;
            _context.ComponentFactories.AddStub<ClayFormActions>();
            _context.ComponentFactories.AddStub<ClayGrid<PhoneGroupMember>>();
            _context.ComponentFactories.AddStub<ClayGrid<PhoneParallelCall>>();
            _context.ComponentFactories.AddStub<ClayGrid<PhoneEquipmentLink>>();
            var services = _context.Services;
            services.AddMudServices();
            services.AddSingleton(clock ?? TimeProvider.System);
            services.AddSingleton<AuthenticationStateProvider, TelAuthentication>();
            services.AddSingleton<PhoneFormService>(Service);
            services.AddScoped(_ => new ClayDraftService(_context.JSInterop.JSRuntime));
            ConfigureTelJournal(_context);
            services.AddSingleton(databaseScript is null ? db : new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => new ScriptedConnection(databaseScript)));
            services.AddSingleton(new ClaySelectAdvancedSearchService(_context.JSInterop.JSRuntime));
            services.AddSingleton(new global::Clayzor.Lib.Web.Controls.ClayErrorService());
            services.AddSingleton(new PhoneTypeSelectDataSource(db));
            services.AddSingleton(new PhoneStationSelectDataSource(db));
            services.AddSingleton(new PhoneEquipmentSelectDataSource(db));
            services.AddSingleton(new PhoneSocketSelectDataSource(db));
            services.AddSingleton(new PhoneCustomerSelectDataSource(db));
            services.AddSingleton(new PhoneEmployeeSelectDataSource(db));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<IServiceProvider>(provider => provider);
            services.AddKeyedSingleton("phoneGroup", new PhoneDirectorySelectDataSource(db) { Mode = PhoneDirectoryMode.Group });
            services.AddKeyedSingleton("drivenBy", new PhoneDirectorySelectDataSource(db) { Mode = PhoneDirectoryMode.DrivenBy });
            services.AddKeyedSingleton("displayedFrom", new PhoneDirectorySelectDataSource(db) { Mode = PhoneDirectoryMode.DisplayedFrom });
            _context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/Telefon/Tel?id=-1&copyId=-1");
            _dialogProvider = _context.Render<MudBlazor.MudDialogProvider>();
            _page = _context.Render<Tel>();
            _tel = _page.Instance;
            Set("_model", new Phone
            {
                Id = 1002, Number = "101", FullNumber = "101", PhoneTypeId = 1, StationId = 1, CustomerId = 1,
                EmployeeId = 4, AtsDescription = "Test", ServicesKind = 1, ViewInDirectory = 1, Connected = "Room",
            });
        }

        internal BunitContext Context => _context;
        internal DelayedPhoneFormService Service { get; }
        internal ClaySelectAdvancedSearchService Finder => _context.Services.GetRequiredService<ClaySelectAdvancedSearchService>();
        internal string WaitForFinderRequest()
        {
            const string method = "claySelectAdvancedSearch.open";
            _page.WaitForAssertion(() => Assert.Single(_context.JSInterop.Invocations[method]));
            var invocation = Assert.Single(_context.JSInterop.Invocations[method]);
            Assert.Equal(true, invocation.Arguments[4]);
            return Assert.IsType<string>(invocation.Arguments[1]);
        }

        internal Phone Model => Get<Phone>("_model");
        internal int PendingCount => Get<IReadOnlyList<PhoneSyncOperation>>("_pendingOperations").Count;

        internal void PrepareWrite()
        {
            Set("JS", new TestJSRuntime());
            Set("_loaded", true);
            Set("_loading", false);
            Set("_canEdit", true);
            Set("CurrentUser", Service.Actor);
            PrimeJournal();
            Model.ChangedAtUtc = new DateTime(2026, 1, 2);
            var original = Model.CopyForNew();
            original.Id = Model.Id;
            original.ChangedAtUtc = Model.ChangedAtUtc;
            Set("_originalPhone", original);
            Service.PersistedSnapshot = new PhoneDeleteSnapshot
            {
                Phone = Model, Station = new PhoneStationLookup { Id = 1, XorcomName = "old" },
            };
        }

        internal void PrepareCloseState(bool draft, bool pendingSync)
        {
            PrepareWrite();
            Set("_operationVersion", Get<int>("_operationVersion") + 1);
            _tel.Id = Model.Id;
            Set("_loadedKey", (Model.Id, _tel.CopyId));
            PrimeJournal();
            Model.PhoneTypeId = null;
            Model.StationId = null;
            Model.CustomerId = null;
            Model.EmployeeId = null;
            Model.DrivenById = null;
            Model.DisplayedFromId = null;
            Set("_selectedType", null);
            Set("_selectedStation", null);
            Set("_selectedEmployee", null);
            Set("_selectedDrivenBy", null);
            Set("_selectedDisplayedFrom", null);
            var original = Model.CopyForNew();
            original.Id = Model.Id;
            original.ChangedAtUtc = Model.ChangedAtUtc;
            Set("_originalPhone", original);
            var relations = Get<PhoneRelationsSnapshot>("_relations");
            Set("_originalRelations", relations.Clone());
            Set("_originalRelationsSnapshot", typeof(Tel).GetMethod("SnapshotRelations", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [relations]));
            Set("_pendingOperations", pendingSync
                ? new[] { new PhoneSyncOperation(PhoneSyncKind.Xorcom, "old") }
                : Array.Empty<PhoneSyncOperation>());
            Service.PersistedSnapshot = new PhoneDeleteSnapshot
            {
                Phone = original, Station = new PhoneStationLookup { Id = 0 }, Relations = relations.Clone(),
            };
            if (draft) Model.Notes = "draft";
        }

        internal void PrepareXorcomStationChange()
        {
            PrepareWrite();
            Set("_originalPhone", new Phone
            {
                Id = Model.Id, ChangedAtUtc = Model.ChangedAtUtc, Number = Model.Number, FullNumber = Model.FullNumber, PhoneTypeId = Model.PhoneTypeId,
                StationId = 1, ServicesKind = Model.ServicesKind, ViewInDirectory = Model.ViewInDirectory, Connected = Model.Connected,
            });
            Set("_originalStation", new PhoneStationLookup { Id = 1, XorcomName = "old" });
            Service.PersistedSnapshot = new PhoneDeleteSnapshot
            {
                Phone = Get<Phone>("_originalPhone"), Station = new PhoneStationLookup { Id = 1, XorcomName = "old" },
            };
            Model.StationId = 2;
            Set("_selectedStation", new PhoneStationLookup { Id = 2, XorcomName = "new" });
        }

        internal void PrepareSameStation(int stationId, string key)
        {
            PrepareWrite();
            Model.StationId = stationId;
            Model.AtsPort = stationId == 32 ? key : string.Empty;
            Model.FullNumber = "1001";
            var original = Model.CopyForNew();
            original.Id = Model.Id;
            original.ChangedAtUtc = Model.ChangedAtUtc;
            Set("_originalPhone", original);
            var station = new PhoneStationLookup { Id = stationId, XorcomName = stationId == 32 ? string.Empty : key };
            Set("_originalStation", station);
            Set("_selectedStation", station);
        }

        internal async Task RestoreWriteJournalAsync(string phase, bool sqlConfirmed, params PhoneSyncOperation[] pending)
        {
            await Invoke("ResetTelJournalAsync");
            var stage = Enum.Parse(typeof(Tel).GetNestedType("TelWriteStage", BindingFlags.NonPublic)!, phase);
            ConfigureTelJournal(_context, new Dictionary<string, string>
            {
                ["tel-progress"] = System.Text.Json.JsonSerializer.Serialize(new
                {
                    AttemptId = FixtureAttemptId, Deleting = false, PhoneId = Model.Id,
                    Stage = stage, Pending = pending, SqlConfirmed = sqlConfirmed, Version = 1, SqlFingerprint = "fixture-sql",
                    LocalProgress = true,
                }),
            });
            await Invoke("AttachTelJournalAsync", Service.Actor, _tel.Id, _tel.CopyId, Get<int>("_operationVersion"));
        }

        internal Task EditNotesAsync(string value, Action? prepare = null) => _page.InvokeAsync(async () =>
        {
            prepare?.Invoke();
            await _page.Find("textarea").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = value });
        });

        internal void WaitForEditorStoreReplace() => _page.WaitForAssertion(() =>
            Assert.Single(_context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]));

        internal void SetupChildren(IReadOnlyDictionary<string, string> scopes, string? rawScope = null)
        {
            ConfigureTelJournal(_context, scopes: scopes);
            _context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope",
                invocation => rawScope is not null && Equals(rawScope, invocation.Arguments[1]))
                .SetResult(new ClayDraftEditorScopeState(11, true));
            _context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], []));
            _context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        }

        internal IRenderedComponent<T> WaitForChild<T>() where T : Microsoft.AspNetCore.Components.IComponent
        {
            _dialogProvider.WaitForAssertion(() => Assert.Single(_dialogProvider.FindComponents<T>()), TimeSpan.FromSeconds(3));
            return _dialogProvider.FindComponent<T>();
        }

        internal Task RespondToChildAsync<T>(IRenderedComponent<T> dialog, string text)
            where T : Microsoft.AspNetCore.Components.IComponent =>
            dialog.InvokeAsync(() => dialog.FindAll("button").Single(button => button.TextContent.Trim() == text).ClickAsync());


        internal async Task CloseDirtyChildAsync<T>(IRenderedComponent<T> editor)
            where T : Microsoft.AspNetCore.Components.IComponent
        {
            var cancel = RespondToChildAsync(editor, "Отмена");
            var confirmation = WaitForChild<ClayEditorCancelDialog>();
            await RespondToChildAsync(confirmation, "Закрыть без сохранения");
            await cancel.WaitAsync(TimeSpan.FromSeconds(3));
        }

        internal void SetupEmptyEditorRegistration() => ConfigureTelJournal(_context);

        internal void WaitForRawRetry() => _page.WaitForAssertion(() =>
        {
            Assert.True(Get<bool>("_telRawPending"));
            Assert.DoesNotContain(_page.FindAll("button"), button => button.TextContent.Trim() == "Повторить восстановление ввода");
        });

        internal Task ClickRawRetryAsync() => _page.InvokeAsync(() =>
            _page.FindAll("button").Single(button => button.TextContent.Trim() == "Обновить").ClickAsync());

        internal void SetupEditorRestore(string json, bool hasRaw, bool rawPending)
        {
            ConfigureTelJournal(_context, new Dictionary<string, string> { ["tel-input"] = json });
            _context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
                .SetResult(new ClayDraftEditorScopeState(11, hasRaw));
            _context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], rawPending ? ["data:tel.number:0"] : []));
        }

        internal void PrimeJournal() =>
            Invoke("AttachTelJournalAsync", Service.Actor, _tel.Id, _tel.CopyId, Get<int>("_operationVersion")).GetAwaiter().GetResult();

        internal string WaitForConfirmation()
        {
            _dialogProvider.WaitForAssertion(() =>
                Assert.Single(_dialogProvider.FindComponents<global::Clayzor.Lib.Web.Controls.Components.ConfirmDialog>()), TimeSpan.FromSeconds(2));
            return _dialogProvider.FindComponent<global::Clayzor.Lib.Web.Controls.Components.ConfirmDialog>().Instance.Message;
        }

        internal void RespondToConfirmation(string buttonText)
        {
            var dialog = _dialogProvider.FindComponent<global::Clayzor.Lib.Web.Controls.Components.ConfirmDialog>();
            dialog.FindAll("button").Single(button => button.TextContent.Trim() == buttonText).Click();
        }

        internal void ClickDeleteToolbarButton()
        {
            Set("_loading", false);
            _context.Renderer.Dispatcher.InvokeAsync(() =>
                typeof(Microsoft.AspNetCore.Components.ComponentBase)
                    .GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tel, null))
                .GetAwaiter().GetResult();
            var button = _page.FindAll("button").Single(item => item.TextContent.Trim() == "Удалить");
            Assert.False(button.HasAttribute("disabled"));
            button.Click();
        }

        internal void WaitForDialogClosed() =>
            _dialogProvider.WaitForAssertion(AssertNoConfirmation, TimeSpan.FromSeconds(2));

        internal void WaitForDeleteCompleted() =>
            _page.WaitForAssertion(() => Assert.True(Get<bool>("_deleted")), TimeSpan.FromSeconds(2));

        internal string CurrentUri =>
            _context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;
        internal string PageMarkup => _page.Markup;
        internal Task ClickJournalButtonAsync(string label) => _page.InvokeAsync(() =>
            _page.FindAll("button").Single(button => button.TextContent.Trim() == label).ClickAsync());

        internal Task LoadRoute(int id, int copyId) => _context.Renderer.Dispatcher.InvokeAsync(() =>
        {
            _tel.Id = id;
            _tel.CopyId = copyId;
            Set("_loadedKey", (id, copyId));
            return Invoke("LoadCurrentAsync", id, copyId, false);
        });

        internal Task NavigateTo(string uri) => _context.Renderer.Dispatcher.InvokeAsync(() =>
            _context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(uri));

        internal void AssertNoConfirmation() =>
            Assert.Empty(_dialogProvider.FindComponents<global::Clayzor.Lib.Web.Controls.Components.ConfirmDialog>());

        internal void SetMember(string name, object? value) => Set(name, value);

        internal T Get<T>(string name) => (T)Member(name).GetValue(_tel)!;

        internal Task Invoke(string name, params object?[] arguments) =>
            _context.Renderer.Dispatcher.InvokeAsync(() =>
                (Task)typeof(Tel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tel, arguments)!);

        internal Task InvokeEvent(string name, params object?[] arguments) =>
            _context.Renderer.Dispatcher.InvokeAsync(() =>
            {
                typeof(Tel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tel, arguments);
            });

        internal string InvokeText(string name, params object?[] arguments) =>
            (string)typeof(Tel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tel, arguments)!;

        private void Set(string name, object? value) => Member(name).SetValue(_tel, value);

        private static MemberAccessor Member(string name) => new(name);

        public async ValueTask DisposeAsync()
        {
            _tel.Dispose();
            await _context.DisposeAsync();
        }

        private sealed class MemberAccessor(string name)
        {
            internal object? GetValue(Tel target)
            {
                var field = typeof(Tel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field is not null) return field.GetValue(target);
                return typeof(Tel).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);
            }

            internal void SetValue(Tel target, object? value)
            {
                var field = typeof(Tel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field is not null)
                {
                    field.SetValue(target, value);
                    return;
                }
                typeof(Tel).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
            }
        }
    }

    private sealed class TelAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, @"DOMAIN\fixture")], "fixture"))));
    }

    private sealed class DelayedPhoneFormService(DbManager db) : PhoneFormService(db)
    {
        private readonly Queue<TaskCompletionSource<PhoneCapabilities>> _capabilities = new();

        internal Dictionary<int, TaskCompletionSource<int?>> Organizations { get; } = new()
        {
            [1] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [2] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        internal TaskCompletionSource<string>? DescriptionDelay { get; set; }
        internal List<PhoneSaveRequest> SaveRequests { get; } = [];
        internal List<string> Calls { get; } = [];
        internal bool ThrowOnSave { get; set; }
        internal Func<Task>? BeforeSaveReply { get; set; }
        internal bool HasWriteConflict { get; set; }
        internal bool ThrowOnDelete { get; set; }
        internal bool FailDeletingXorcom { get; set; }
        internal bool DenyAts { get; set; }
        internal List<(string? Principal, int EmployeeId)> AtsActors { get; } = [];
        internal List<string> AtsAttemptIds { get; } = [];
        internal List<string> WorkflowEvents { get; } = [];
        internal List<(bool Pre, int Index)> WorkflowOrdinals { get; } = [];
        internal List<PhoneValidationIssue> ValidationIssues { get; } = [];
        internal string? SavedFullNumber { get; set; }
        internal PhoneDeleteSnapshot? PersistedSnapshot { get; set; }
        internal int LoadCalls { get; private set; }
        internal Func<int, bool, PhoneFormData>? LoadData { get; set; }
        internal Func<int, Task<PhoneCapabilities>>? InitialCapabilities { get; set; }

        internal Employee Actor { get; } = AuthorizedActor();
        internal Func<Employee?>? CurrentActor { get; set; }
        internal Func<Task<Employee?>>? CurrentActorLookup { get; set; }

        public override Task<Employee?> GetCurrentUserAsync(string? windowsPrincipal, CancellationToken cancellationToken = default) =>
            CurrentActorLookup?.Invoke() ?? Task.FromResult(CurrentActor is null ? Actor : CurrentActor());

        private static Employee AuthorizedActor()
        {
            var actor = new Employee { Id = 4 };
            actor.SetRoleGrants([new EmployeeRoleGrant
            {
                RoleId = (int)Роли.Специалист_по_инвентаризации_оборудования,
                PersonId = 0,
            }]);
            return actor;
        }

        public override Task<PhoneFormData?> GetAsync(int id, bool copy = false, Employee? currentUser = null)
        {
            LoadCalls++;
            Calls.Add("load");
            return Task.FromResult<PhoneFormData?>(LoadData?.Invoke(id, copy) ?? new PhoneFormData
            {
                Phone = new Phone { Id = copy ? 0 : id, Number = "reloaded", FullNumber = "reloaded", ChangedAtUtc = new DateTime(2026, 1, 2) }, CanEdit = true,
            });
        }

        internal TaskCompletionSource<PhoneCapabilities> EnqueueCapabilities()
        {
            var result = new TaskCompletionSource<PhoneCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
            _capabilities.Enqueue(result);
            return result;
        }

        public override Task<PhoneCapabilities> GetCapabilitiesAsync(int? stationId, int? phoneTypeId, int phoneId) =>
            InitialCapabilities?.Invoke(phoneId) ?? (_capabilities.Count > 0 ? _capabilities.Dequeue().Task : Task.FromResult(new PhoneCapabilities { HasPorts = stationId == 32 }));

        public override Task<IReadOnlyList<PhoneValidationIssue>> ValidateServerAsync(PhoneSaveRequest request) =>
            Task.FromResult<IReadOnlyList<PhoneValidationIssue>>(ValidationIssues);

        public override async Task<PhoneSaveResult> SaveAsync(PhoneSaveRequest request, string? windowsPrincipal, int employeeId,
            Guid attemptId, TelSqlBoundary? boundary = null)
        {
            if (SavedFullNumber is not null) request.Phone.FullNumber = SavedFullNumber;
            if (boundary is not null)
            {
                await boundary.Prepare();
                boundary.Dispatch();
            }
            Calls.Add("save");
            if (BeforeSaveReply is not null)
                await BeforeSaveReply();
            if (ThrowOnSave) throw new InvalidOperationException("SQL Save failed");
            SaveRequests.Add(request);
            if (HasWriteConflict)
                return new PhoneSaveResult
                {
                    PhoneId = request.Phone.Id, VersionConflict = true,
                    Issues = [new PhoneValidationIssue("OriginalPhone", "Конфликт версий")],
                };
            return new PhoneSaveResult { PhoneId = request.Phone.Id, Saved = true };
        }

        public override Task<PhoneDeleteSnapshot?> GetDeleteSnapshotAsync(int id)
        {
            Calls.Add("read");
            return Task.FromResult(PersistedSnapshot);
        }

        public override Task ValidateDeleteProposalAsync(Phone original, string? principal, int employeeId, CancellationToken ct) =>
            Task.CompletedTask;

        public override async Task DeleteAsync(Phone original, string? windowsPrincipal, int employeeId,
            Guid attemptId, TelSqlBoundary? boundary = null)
        {
            if (boundary is not null)
            {
                await boundary.Prepare();
                boundary.Dispatch();
            }
            Calls.Add("delete");
            if (ThrowOnDelete) throw new InvalidOperationException("SQL Delete failed");
        }

        internal PhoneSyncPhase? NextAtsPhase { get; set; }

        internal override async Task<PhoneSyncOperationResult> SynchronizeWorkflowStepAsync(Guid attemptId, string? principal,
            int employeeId, int phoneId, bool beforeSql, int stepIndex, PhoneSyncOperation operation, CancellationToken ct,
            Func<PhoneSyncOperation, Task>? prepareDispatch = null)
        {
            WorkflowEvents.Add(beforeSql ? $"pre:{stepIndex}" : $"post:{stepIndex}");
            WorkflowOrdinals.Add((beforeSql, stepIndex));
            var results = await SynchronizeTelPhasedAsync(attemptId.ToString("N"), principal, employeeId, phoneId,
                [operation], ct, prepareDispatch);
            return results.Single();
        }

        internal override async Task<IReadOnlyList<PhoneSyncOperationResult>> SynchronizeTelPhasedAsync(
            string attemptId, string? windowsPrincipal, int employeeId, int phoneId, IEnumerable<PhoneSyncOperation> operations,
            CancellationToken cancellationToken, Func<PhoneSyncOperation, Task>? prepareDispatch = null)
        {
            AtsActors.Add((windowsPrincipal, employeeId));
            AtsAttemptIds.Add(attemptId);
            var results = new List<PhoneSyncOperationResult>();
            foreach (var operation in operations)
            {
                if (DenyAts)
                {
                    results.Add(new PhoneSyncOperationResult(attemptId, operation, PhoneSyncPhase.Rejected, "отказ авторизации"));
                    continue;
                }
                var phase = NextAtsPhase ?? (FailDeletingXorcom && operation.Kind == PhoneSyncKind.Xorcom && operation.Deleting
                    ? PhoneSyncPhase.Failed : PhoneSyncPhase.Succeeded);
                NextAtsPhase = null;
                if (phase is not (PhoneSyncPhase.NotSent or PhoneSyncPhase.Rejected))
                {
                    if (prepareDispatch is not null)
                        await prepareDispatch(operation);
                    Calls.Add($"sync:{operation.Key}:{operation.Deleting}");
                }
                results.Add(new PhoneSyncOperationResult(attemptId, operation, phase));
            }
            return results;
        }

        public override Task<int?> GetEmployeeOrganizationAsync(int employeeId) => Organizations[employeeId].Task;

        public override Task<string> GetAtsDescriptionAsync(int employeeId, bool upperCase) =>
            DescriptionDelay?.Task ?? Task.FromResult($"Employee{employeeId}");

        public override Task<(int Id, string Number)?> GetDefaultEmployeePhoneAsync(int employeeId) =>
            Task.FromResult<(int, string)?>((employeeId, $"10{employeeId}"));
    }

    private static void ConfigureTelJournal(BunitContext context, IReadOnlyDictionary<string, string>? scopes = null)
    {
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(new ClayDraftRestore("tel-fixture", scopes is { Count: > 0 }, 0, null, scopes));
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", call => call.Arguments.Count == 2).SetResult(true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, 1, 1));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(11);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(11, false));
    }

    private const string FixtureAttemptId = "10000000000000000000000000000001";

    private sealed class TestJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
