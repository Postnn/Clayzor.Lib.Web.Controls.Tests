using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Documents;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Locations;
using Clayzor.Lib.Web.DBSelect.Persons;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using MudBlazor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Extensions;
using MudBlazor.Services;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Профильные тесты жизненного цикла формы редактирования оборудования.</summary>
public sealed class EquipmentFormTests
{
    /// <summary>Применить сохраняет запись, обновляет opener и не повторяет UPDATE без изменений.</summary>
    [Fact]
    public async Task Apply_SavesOnceAndRefreshesOpener()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_UPDATED");

        await InvokeSave(page, false);
        await InvokeSave(page, false);

        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.Equal("CLAY_TEST_UPDATED", fixture.Service.Updated?.SerialNumber);
        Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>SQL-ошибка сохраняет ввод и не выполняет success-path.</summary>
    [Fact]
    public async Task SaveSqlException_KeepsInputAndSkipsSuccessCallbacks()
    {
        var fixture = CreateFixture();
        fixture.Service.SaveException = SqlExceptionFactory.Create(50000, "trigger");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_FAILED");

        await InvokeSave(page, false);

        page.WaitForAssertion(() => Assert.Contains("Оборудование не сохранено", page.Markup));
        Assert.Equal("CLAY_TEST_FAILED", FindSn(page).GetAttribute("value"));
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Параллельный повторный Save не вызывает второй UPDATE.</summary>
    [Fact]
    public async Task ConcurrentSave_ExecutesSingleUpdate()
    {
        var fixture = CreateFixture();
        fixture.Service.DeferredUpdate = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_RACE");

        var first = InvokeSave(page, false);
        fixture.Service.WaitForUpdateRequest();
        var second = InvokeSave(page, false);
        fixture.Service.UpdateCompletion.SetResult();
        await first;
        await second;

        Assert.Equal(1, fixture.Service.UpdateCalls);
    }

    /// <summary>Смена id на том же маршруте загружает новую запись без ручного lifecycle-вызова.</summary>
    [Fact]
    public async Task QueryIdChange_LoadsNewEquipmentThroughRouter()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));

        WaitForSn(page, "CLAY_TEST_8");
        Assert.Equal(new[] { 7, 8 }, fixture.Service.Requests);
    }

    /// <summary>Для обычного оборудования дата списания требует документ, для SIM — нет.</summary>
    [Fact]
    public async Task WriteOffValidation_EnforcesDocumentExceptForSim()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_writeOffDate", new DateTime(2026, 3, 29));

        Assert.False(ValidateBusinessRules(page.Instance));
        CurrentModel(page.Instance).EquipmentTypeId = 27;
        Assert.True(ValidateBusinessRules(page.Instance));
        CurrentModel(page.Instance).DocumentWriteOffId = 9;
        SetPrivate(page.Instance, "_writeOffDate", null);
        Assert.False(ValidateBusinessRules(page.Instance));
    }

    /// <summary>Завершение отложенной загрузки после Dispose не меняет состояние формы.</summary>
    [Fact]
    public async Task DisposeDuringLoad_LateResultIsIgnored()
    {
        var fixture = CreateFixture();
        fixture.Service.DeferredInitialLoad = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        fixture.Service.WaitForGetRequest();

        page.Instance.Dispose();
        fixture.Service.InitialLoadCompletion.SetResult();
        await fixture.Service.InitialLoadReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await page.InvokeAsync(() => { });

        Assert.True(PrivateBool(page.Instance, "_disposed"));
        Assert.False(PrivateBool(page.Instance, "_loaded"));
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    /// <summary>Обновление текущих detail-метаданных не меняет признак несохранённой основной формы.</summary>
    [Fact]
    public async Task DetailMetadataChanges_DoNotMarkMainFormModified()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.False(IsModified(page.Instance));
        CurrentModel(page.Instance).LocationId = 4;
        CurrentModel(page.Instance).EmployeeId = 4;

        Assert.False(IsModified(page.Instance));
    }

    /// <summary>Добавление и удаление номера используют атомарный phone-save и синхронизацию.</summary>
    [Fact]
    public async Task PhoneAddAndDelete_UsesAtomicDiffAndSynchronizes()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_phoneToAdd", 11);
        await page.InvokeAsync(async () =>
        {
            var method = typeof(EquipmentForm).GetMethod("AddPhoneAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, null) is Task task)
                await task;
        });
        page.WaitForAssertion(() => Assert.Contains("101", page.Markup));
        Assert.Equal(8, ((List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_phones")!)[0].ConnectionEquipmentId);

        await InvokeSave(page, false);
        Assert.Equal(1, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(1, fixture.Service.SyncCalls);
        Assert.Equal(new[] { 11 }, fixture.Service.PhoneSaves[0].Current);

        var phones = (List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_phones")!;
        phones.Clear();
        await InvokeSave(page, false);

        Assert.Equal(2, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(2, fixture.Service.SyncCalls);
        Assert.Equal(new[] { 11 }, fixture.Service.PhoneSaves[1].Original);
        Assert.Empty(fixture.Service.PhoneSaves[1].Current);
    }

    /// <summary>Форма использует всю ширину, legacy-порядок команд и отображает ФИО изменившего.</summary>
    [Fact]
    public async Task Layout_UsesFullWidthLegacyOrderAndChangedEmployeeName()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        var style = page.Find(".equipment-form").GetAttribute("style") ?? string.Empty;
        Assert.Contains("max-width:100%", style.Replace(" ", string.Empty));
        var commands = page.FindAll("button").Select(button => button.TextContent.Trim()).Take(5).ToArray();
        Assert.Equal(new[] { "OK", "Применить", "Копировать", "Обновить", "Отмена" }, commands);
        var pairs = page.FindAll(".equipment-form-pair");
        Assert.Contains("equipment-form-writeoff-row", pairs[0].ClassList);
        Assert.Contains("SN:", pairs[0].TextContent);
        Assert.Contains("Списано:", pairs[0].TextContent);
        Assert.Contains("Тип:", pairs[1].TextContent);
        Assert.Contains("Модель:", pairs[1].TextContent);
        Assert.Equal("Тестов А.", page.Find(".clay-changed-user").TextContent.Trim());
        Assert.Equal("http://localhost/user?id=4", page.Find(".clay-changed-user").GetAttribute("href"));
        Assert.Contains(page.FindAll("input"), input => input.GetAttribute("value") == "001122334455");
        Assert.DoesNotContain(page.FindAll("input"), input => input.GetAttribute("value")?.StartsWith("_model.MacAddress") == true);
    }

    /// <summary>Сетевая история загружается для оборудования с сетевым именем.</summary>
    [Fact]
    public async Task NetworkHistory_LoadsForNetworkEquipment()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.Contains(page.FindAll("button"), button => button.TextContent.Trim() == "История имён");
        var history = (IReadOnlyList<EquipmentNetworkNameHistory>)GetPrivate(page.Instance, "_networkNameHistory")!;
        Assert.Equal("old-host", Assert.Single(history).NetworkName);
    }

    /// <summary>Открытое из формы перемещение «На руках» требует сотрудника и сохраняет обе связи.</summary>
    [Fact]
    public async Task OnHandsLocationFromForm_RequiresAndSavesResponsible()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenLocationDialogAsync", (object?)null);
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<EquipmentLocationDialog>()));
        var dialog = provider.FindComponent<EquipmentLocationDialog>();
        Assert.True(dialog.Instance.RequireResponsible);

        await page.InvokeAsync(() => reference.Close(DialogResult.Ok(
            new EquipmentLocationDialogResult(55, DateTime.Now.AddMinutes(-1), null, "", null))));
        await pending;
        Assert.Empty(fixture.Service.LocationSaves);
        Assert.Contains("укажите ответственного сотрудника", page.Markup);

        dialogAdded = TrackNextDialog(context);
        pending = InvokeHandler(page, "OpenLocationDialogAsync", (object?)null);
        reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await page.InvokeAsync(() => reference.Close(DialogResult.Ok(
            new EquipmentLocationDialogResult(55, DateTime.Now.AddMinutes(-1), null, "", 4))));
        await pending;

        var save = Assert.Single(fixture.Service.LocationSaves);
        Assert.Equal(55, save.Location.LocationId);
        Assert.Equal(4, save.Responsible?.EmployeeId);
    }

    /// <summary>Диалог прежней записи после смены id не выполняет SQL для новой записи.</summary>
    [Fact]
    public async Task AccountDialogFromPreviousId_DoesNotSaveIntoNewEquipment()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenAccountDialogAsync", (object?)null);
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        dialog.Close(DialogResult.Ok(new EquipmentAccountDialogResult("account-from-a", "", false, "")));
        await pending;

        Assert.Equal(0, fixture.Service.AddAccountCalls);
    }

    /// <summary>Подтверждение удаления SIM прежней записи аннулируется после смены id.</summary>
    [Fact]
    public async Task DeleteSimConfirmationFromPreviousId_DoesNotDeleteNewEquipment()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canEditSim", true);
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "DeleteSimAsync");
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        dialog.Close(DialogResult.Ok(true));
        await pending;

        Assert.Equal(0, fixture.Service.DeleteSimCalls);
    }

    /// <summary>Поздний JS-ответ загрузки A не меняет поля полностью загруженной записи B.</summary>
    [Fact]
    public async Task LateTimeZoneResultFromPreviousLoad_DoesNotChangeCurrentEquipment()
    {
        var fixture = CreateFixture();
        var dateA = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var dateB = new DateTime(2026, 7, 20, 15, 30, 0, DateTimeKind.Utc);
        var sectionDateA = new DateTime(2026, 2, 11, 9, 45, 0, DateTimeKind.Utc);
        fixture.Service.WriteOffDates[7] = dateA;
        fixture.Service.WriteOffDates[8] = dateB;
        fixture.Service.StoredLocations.Add(new EquipmentLocationPeriod
        {
            EquipmentId = 7, LocationId = 55, FromUtc = sectionDateA,
        });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        fixture.Js.DeferredIdentifier = "clayGridTimeZone.getOffsetMinutes";
        fixture.Js.DeferredArgument = sectionDateA.ToString("O");
        var loadA = InvokeHandler(page, "LoadCurrentAsync", 7);
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        fixture.Js.Completion.SetResult();
        await loadA;

        Assert.Equal(8, CurrentModel(page.Instance).Id);
        Assert.Equal(8, CurrentModel(page.Instance).TenantId);
        Assert.Equal(dateB.AddMinutes(180).Date, GetPrivate(page.Instance, "_writeOffDate"));
        var localDates = (Dictionary<DateTime, DateTime>)GetPrivate(page.Instance, "_localDates")!;
        Assert.False(localDates.ContainsKey(sectionDateA));
        Assert.False(IsModified(page.Instance));
    }

    /// <summary>Закрытие формы во время отложенного callback после SQL завершает Save без исключения.</summary>
    [Fact]
    public async Task DisposeDuringRefreshCallback_AfterSuccessfulSaveIsObserved()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_CALLBACK");
        fixture.Js.DeferredIdentifier = "clayModelEquipmentForm.refreshOpener";
        var save = InvokeSave(page, false);
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        page.Instance.Dispose();
        fixture.Js.Completion.SetException(new OperationCanceledException());
        await save;

        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.True(PrivateBool(page.Instance, "_disposed"));
    }

    /// <summary>SQL-ошибка изменения ответственного не меняет отображаемый исходный объект.</summary>
    [Fact]
    public async Task ResponsibleSqlException_KeepsDisplayedRowUnchanged()
    {
        var fixture = CreateFixture();
        var originalDate = new DateTime(2026, 11, 20);
        var responsible = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7, EmployeeId = 4, FromUtc = new DateTime(2026, 11, 1),
            ReturnDate = originalDate, Notes = "old",
        };
        fixture.Service.StoredResponsibles.Add(responsible);
        fixture.Service.ResponsibleException = SqlExceptionFactory.Create(50000, "trigger");
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenResponsibleDialogAsync", EquipmentResponsibleDialogMode.Edit, responsible);
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => dialog.Close(DialogResult.Ok(new EquipmentResponsibleDialogResult(
            4, responsible.FromUtc, null, originalDate.AddDays(1), "new", null))));
        await pending;

        Assert.Equal(originalDate, responsible.ReturnDate);
        Assert.Equal("old", responsible.Notes);
        Assert.Equal(originalDate.AddDays(1), fixture.Service.ResponsibleUpdated?.ReturnDate);
        Assert.Contains("Ответственный не сохранён", page.Markup);
    }

    /// <summary>Обновление бухучёта сохраняет изменённые владельца, арендатора и документы основной формы.</summary>
    [Fact]
    public async Task RefreshAccounting_PreservesModifiedMainFields()
    {
        var fixture = CreateFixture();
        fixture.Service.StoredAccounting.Add(new EquipmentAccounting
        {
            EquipmentId = 7, OwnerId = 20, TenantId = 30, ReceiptDocumentId = 40, WriteOffDocumentId = 50,
        });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        CurrentModel(page.Instance).TenantId = 999;
        CurrentModel(page.Instance).DocumentReceiptOuId = 888;

        await InvokeHandler(page, "RefreshAccountingAsync");

        Assert.Equal(20, CurrentModel(page.Instance).OwnerId);
        Assert.Equal(999, CurrentModel(page.Instance).TenantId);
        Assert.Equal(888, CurrentModel(page.Instance).DocumentReceiptOuId);
        Assert.Equal(50, CurrentModel(page.Instance).DocumentWriteOffId);
    }

    /// <summary>Восстановление того же типа после Escape не перечитывает и не очищает телефонные правки.</summary>
    [Fact]
    public async Task RestoringSameType_PreservesEditedPhoneList()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var phones = (List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_phones")!;
        phones.Add(new EquipmentPhoneLink { PhoneId = 11, EquipmentId = 7, Notes = "edited" });
        var reads = fixture.Service.GetPhonesCalls;
        var selectedType = GetPrivate(page.Instance, "_selectedType");

        await InvokeHandler(page, "OnTypeChanged", selectedType);

        Assert.Single(phones);
        Assert.Equal("edited", phones[0].Notes);
        Assert.Equal(reads, fixture.Service.GetPhonesCalls);
    }

    /// <summary>Кнопка конфигурации следует роли 102 независимо от роли редактирования 43.</summary>
    [Fact]
    public async Task PhoneConfigAccess_UsesRole102InsteadOfEditRole()
    {
        var fixture = CreateFixture(phoneConfig: true);
        fixture.Service.Roles.Clear();
        fixture.Service.Roles.Add(102);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var button = page.FindAll("button").Single(item => item.TextContent.Trim() == "Конфигурация");
        Assert.False(button.HasAttribute("disabled"));

        fixture.Service.Roles.Clear();
        fixture.Service.Roles.Add(43);
        await InvokeHandler(page, "LoadCurrentAsync", 7);
        button = page.FindAll("button").Single(item => item.TextContent.Trim() == "Конфигурация");
        Assert.True(button.HasAttribute("disabled"));
    }

    private static Fixture CreateFixture(bool phones = false, bool network = false, bool phoneConfig = false)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        var js = new DeferredJSRuntime(context.JSInterop.JSRuntime);
        context.Services.AddSingleton<IJSRuntime>(js);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", _ => true).SetResult(180);
        context.JSInterop.Setup<long>("clayGridTimeZone.localDateTimeToUtcMilliseconds", _ => true).SetResult(0L);
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=7");
        var configuration = new ConfigurationManager
        {
            ["URI_user_form"] = "http://localhost/user",
            ["URI_model_form"] = "http://localhost/Equipment/ModelEquipmentForm",
        };
        var service = new FakeEquipmentFormService(CreateDb()) { PhoneConfig = phoneConfig };
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton<EquipmentFormService>(service);
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(CreateDb(
            Script.Rows(TypeRow(phones, network, phoneConfig)), Script.Rows(TypeRow(phones, network, phoneConfig)),
            Script.Rows(TypeRow(phones, network, phoneConfig)), Script.Rows(TypeRow(phones, network, phoneConfig)))));
        context.Services.AddSingleton(new EquipmentModelSelectDataSource(CreateDb(
            Script.Rows(ModelRow(network, phoneConfig)),
            Script.Rows(ModelRow(network, phoneConfig)),
            Script.Rows(ModelRow(network, phoneConfig)),
            Script.Rows(ModelRow(network, phoneConfig)))));
        context.Services.AddSingleton(new PhonePoolSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new PhoneNumberSelectDataSource(CreateDb(
            Script.Rows(PhoneNumberRow()), Script.Rows(PhoneNumberRow()), Script.Rows(PhoneNumberRow()))));
        context.Services.AddSingleton(new PersonSelectDataSource(CreateDb(
            Script.Rows(PersonRow()), Script.Rows(PersonRow()), Script.Rows(PersonRow()), Script.Rows(PersonRow()))));
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("receipt", new DocumentSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("writeoff", new DocumentSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<UpsEquipmentSelectDataSource>("ups", new UpsEquipmentSelectDataSource(CreateDb(), true));
        context.Services.AddKeyedSingleton<UpsEquipmentSelectDataSource>("server", new UpsEquipmentSelectDataSource(CreateDb(), false));
        return new Fixture(context, service, navigation, js);
    }

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));

    private static AngleSharp.Dom.IElement FindSn(IRenderedComponent<EquipmentForm> page) =>
        page.FindAll("input").Single(input => input.GetAttribute("value")?.StartsWith("CLAY_TEST") == true);

    private static void WaitForSn(IRenderedComponent<EquipmentForm> page, string value) =>
        page.WaitForAssertion(() => Assert.Equal(value, FindSn(page).GetAttribute("value")), TimeSpan.FromSeconds(3));

    private static async Task InvokeSave(IRenderedComponent<EquipmentForm> page, bool close) =>
        await page.InvokeAsync(async () =>
        {
            var method = typeof(EquipmentForm).GetMethod("SaveAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, new object[] { close }) is Task task)
                await task;
        });

    private static TaskCompletionSource<IDialogReference> TrackNextDialog(BunitContext context)
    {
        var result = new TaskCompletionSource<IDialogReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Services.GetRequiredService<IDialogService>().DialogInstanceAddedAsync += reference =>
        {
            result.TrySetResult(reference);
            return Task.CompletedTask;
        };
        return result;
    }

    private static async Task InvokeHandler(
        IRenderedComponent<EquipmentForm> page,
        string name,
        params object?[] args) =>
        await page.InvokeAsync(async () =>
        {
            var method = typeof(EquipmentForm).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, args) is Task task)
                await task;
        });

    private static EquipmentEntity CurrentModel(EquipmentForm page) =>
        (EquipmentEntity)GetPrivate(page, "_model")!;

    private static object? GetPrivate(EquipmentForm page, string name) => typeof(EquipmentForm)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page);

    private static void SetPrivate(EquipmentForm page, string name, object? value) => typeof(EquipmentForm)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .SetValue(page, value);

    private static bool PrivateBool(EquipmentForm page, string name) => (bool)GetPrivate(page, name)!;

    private static bool IsModified(EquipmentForm page) => (bool)typeof(EquipmentForm)
        .GetProperty("IsModified", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page)!;

    private static bool ValidateBusinessRules(EquipmentForm page) => (bool)typeof(EquipmentForm)
        .GetMethod("ValidateBusinessRules", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(page, null)!;

    private static Dictionary<string, object?> TypeRow(bool phones = false, bool network = false, bool phoneConfig = false) => Script.Row(
        ("КодТипаОборудования", phoneConfig ? 30 : 103), ("ТипОборудования", phoneConfig ? "IP-телефон" : "Шторы"),
        ("SNобязателен", (byte)0), ("ЕстьХарактеристикиМонитора", (byte)0),
        ("ЕстьТелефонныйНомер", phones ? (byte)1 : (byte)0), ("ЕстьMACадрес", (byte)0),
        ("ЕстьСетевоеИмя", network ? (byte)1 : (byte)0));

    private static Dictionary<string, object?> ModelRow(bool network = false, bool phoneConfig = false) => Script.Row(
        ("КодМоделиОборудования", 945), ("МодельОборудования", phoneConfig ? "Phone" : "Шторы"),
        ("КодТипаОборудования", phoneConfig ? 30 : 103), ("Net", network ? 1 : 0),
        ("ШаблонТелефона", phoneConfig ? "template" : ""));

    private static Dictionary<string, object?> PhoneNumberRow() => Script.Row(
        ("КодТелефонногоНомера", 11), ("Телефон", "101"),
        ("КодТелефоннойСтанции", 2), ("XorcomName", ""));

    private static Dictionary<string, object?> PersonRow() => Script.Row(
        ("КодЛица", 1), ("Кличка", "Тестовая организация"));

    private sealed record Fixture(
        BunitContext Context,
        FakeEquipmentFormService Service,
        FakeNavigationManager Navigation,
        DeferredJSRuntime Js);

    private sealed class FakeEquipmentFormService : EquipmentFormService
    {
        private readonly TaskCompletionSource _getRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _updateRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeEquipmentFormService(DbManager db) : base(db) { }
        public EquipmentEntity? Updated { get; private set; }
        public Exception? SaveException { get; set; }
        public Exception? ResponsibleException { get; set; }
        public EquipmentResponsiblePeriod? ResponsibleUpdated { get; private set; }
        public bool DeferredUpdate { get; set; }
        public bool DeferredInitialLoad { get; set; }
        public bool PhoneConfig { get; set; }
        public int UpdateCalls { get; private set; }
        public int AddAccountCalls { get; private set; }
        public int DeleteSimCalls { get; private set; }
        public int GetPhonesCalls { get; private set; }
        public List<int> Requests { get; } = [];
        public HashSet<int> Roles { get; } = [42, 43, 102];
        public Dictionary<int, DateTime?> WriteOffDates { get; } = [];
        public List<EquipmentResponsiblePeriod> StoredResponsibles { get; } = [];
        public List<EquipmentLocationPeriod> StoredLocations { get; } = [];
        public List<(EquipmentLocationPeriod Location, EquipmentResponsiblePeriod? Responsible)> LocationSaves { get; } = [];
        public List<EquipmentAccounting> StoredAccounting { get; } = [];
        public List<(EquipmentResponsiblePeriod Current, EquipmentResponsiblePeriod? Next, EquipmentLocationPeriod? Location)> WithdrawCalls { get; } = [];
        public List<EquipmentPhoneLink> StoredPhones { get; private set; } = [];
        public List<(int[] Original, int[] Current)> PhoneSaves { get; } = [];
        public int UpdateWithPhonesCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public TaskCompletionSource UpdateCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialLoadCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialLoadReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<EquipmentEntity?> GetAsync(int id)
        {
            Requests.Add(id);
            if (DeferredInitialLoad && id == 7)
            {
                _getRequested.TrySetResult();
                await InitialLoadCompletion.Task;
                InitialLoadReturned.TrySetResult();
            }
            return new EquipmentEntity
            {
                Id = id, EquipmentTypeId = PhoneConfig ? 30 : 103, ModelId = 945, OwnerId = 1, TenantId = id,
                LocationId = 55, SerialNumber = $"CLAY_TEST_{id}", Description = $"Copy{id}",
                MacAddress = "001122334455", WriteOffAtUtc = WriteOffDates.GetValueOrDefault(id),
                ChangedById = 4, ChangedAtUtc = new DateTime(2026, 11, 18, 8, 0, 0), ChangedByName = "Тестов А.",
            };
        }

        public override Task<IReadOnlyList<EquipmentResponsiblePeriod>> GetResponsiblesAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentResponsiblePeriod>>(StoredResponsibles.ToList());

        public override Task<IReadOnlyList<EquipmentAccount>> GetAccountsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentAccount>>([]);

        public override Task<IReadOnlyList<EquipmentSimCard>> GetSimCardsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentSimCard>>([]);

        public override Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
            int equipmentId, bool showHistory, bool showRequired) =>
            Task.FromResult<IReadOnlyList<EquipmentSimService>>([]);

        public override Task<IReadOnlyList<SimServiceType>> GetSimServiceTypesAsync() =>
            Task.FromResult<IReadOnlyList<SimServiceType>>([]);

        public override Task<IReadOnlyList<EquipmentLicense>> GetLicensesAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentLicense>>([]);

        public override Task<IReadOnlyList<LicenseType>> GetLicenseTypesAsync() =>
            Task.FromResult<IReadOnlyList<LicenseType>>([]);

        public override Task<IReadOnlyList<EquipmentSoftwareInstallation>> GetSoftwareInstallationsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentSoftwareInstallation>>([]);

        public override Task<IReadOnlyList<EquipmentAccounting>> GetAccountingAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentAccounting>>(StoredAccounting.ToList());

        public override Task<EquipmentRouterInfo?> GetRouterInfoAsync(int equipmentId) =>
            Task.FromResult<EquipmentRouterInfo?>(null);

        public override Task<IReadOnlyList<EquipmentRouterInventoryItem>> GetRouterInventoryAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentRouterInventoryItem>>([]);

        public override Task<IReadOnlyList<UpsServerLink>> GetUpsLinksAsync(int serverEquipmentId) =>
            Task.FromResult<IReadOnlyList<UpsServerLink>>([]);

        public override Task<EquipmentActualInventory?> GetLastInventoryAsync(int equipmentId) =>
            Task.FromResult<EquipmentActualInventory?>(null);

        public override Task<int> GetNetworkAttachmentCountAsync(int equipmentId) => Task.FromResult(0);

        public override Task<IReadOnlyList<EquipmentNetworkNameHistory>> GetNetworkNameHistoryAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentNetworkNameHistory>>(
            [
                new() { EquipmentId = equipmentId, NetworkName = "old-host", ChangedAtUtc = new DateTime(2026, 11, 18), ChangedByName = "Тестовый сотрудник" },
            ]);

        public override Task<bool> CanUseAccountingAsync(int? personId) => Task.FromResult(true);

        public override Task<bool> HasRoleAsync(int roleId) => Task.FromResult(Roles.Contains(roleId));

        public override Task<bool> HasRoleForPersonAsync(int roleId, int? personId) => Task.FromResult(true);

        public override async Task UpdateAsync(EquipmentEntity equipment)
        {
            UpdateCalls++;
            Updated = equipment;
            if (DeferredUpdate)
            {
                _updateRequested.TrySetResult();
                await UpdateCompletion.Task;
            }
            if (SaveException is not null)
                throw SaveException;
        }

        public override Task AddAccountAsync(EquipmentAccount account)
        {
            AddAccountCalls++;
            return Task.CompletedTask;
        }

        public override Task DeleteSimAsync(int equipmentId)
        {
            DeleteSimCalls++;
            return Task.CompletedTask;
        }

        public override Task AddLocationAsync(
            EquipmentLocationPeriod location,
            EquipmentResponsiblePeriod? responsible = null)
        {
            LocationSaves.Add((location, responsible));
            return Task.CompletedTask;
        }

        public override Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible)
        {
            ResponsibleUpdated = responsible;
            if (ResponsibleException is not null)
                throw ResponsibleException;
            return Task.CompletedTask;
        }

        public override Task WithdrawResponsibleAsync(
            EquipmentResponsiblePeriod current,
            EquipmentResponsiblePeriod? next,
            EquipmentLocationPeriod? location)
        {
            WithdrawCalls.Add((current, next, location));
            return Task.CompletedTask;
        }

        public override Task<int?> GetConnectionEquipmentIdAsync(int phoneId, int? employeeId, int locationId) =>
            Task.FromResult<int?>(8);

        public override Task<IReadOnlyList<EquipmentLocationPeriod>> GetLocationsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentLocationPeriod>>(
                StoredLocations.Where(item => item.EquipmentId == equipmentId).ToList());

        public override Task<bool> HasCurrentResponsibleAsync(int equipmentId) => Task.FromResult(false);

        public override Task<IReadOnlyList<EquipmentPhoneLink>> GetPhonesAsync(int equipmentId)
        {
            GetPhonesCalls++;
            return Task.FromResult<IReadOnlyList<EquipmentPhoneLink>>(StoredPhones.ToList());
        }

        public override Task UpdateWithPhonesAsync(
            EquipmentEntity equipment,
            IReadOnlyCollection<EquipmentPhoneLink> original,
            IReadOnlyCollection<EquipmentPhoneLink> current)
        {
            UpdateWithPhonesCalls++;
            Updated = equipment;
            StoredPhones = current.ToList();
            PhoneSaves.Add((original.Select(item => item.PhoneId).ToArray(), current.Select(item => item.PhoneId).ToArray()));
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<string>> SynchronizePhonesAsync(
            int equipmentId,
            IReadOnlyCollection<EquipmentPhoneLink> original,
            IReadOnlyCollection<EquipmentPhoneLink> current)
        {
            SyncCalls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public void WaitForGetRequest()
        {
            for (var attempt = 0; attempt < 100 && !_getRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_getRequested.Task.IsCompleted, "Отложенная загрузка не была начата");
        }

        public void WaitForUpdateRequest()
        {
            for (var attempt = 0; attempt < 100 && !_updateRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_updateRequested.Task.IsCompleted, "Отложенное сохранение не было начато");
        }
    }

    private sealed class DeferredJSRuntime(IJSRuntime inner) : IJSRuntime
    {
        public string? DeferredIdentifier { get; set; }
        public string? DeferredArgument { get; set; }
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == DeferredIdentifier &&
                (DeferredArgument is null || Equals(args?.FirstOrDefault(), DeferredArgument)))
            {
                Requested.TrySetResult();
                await Completion.Task.WaitAsync(cancellationToken);
            }
            return await inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }
    }
}
