using System.Reflection;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.DBSelect.Phones;
using Kesco.App.Web.Inventory.Components.Pages.Telefon;
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

        Assert.Equal(["sync:old:True", "save", "sync:new:False"], fixture.Service.Calls);
        Assert.Equal(1, fixture.Service.Calls.Count(call => call == "sync:old:True"));
    }

    /// <summary>Ошибка SQL после очистки старой Xorcom вызывает компенсационное восстановление.</summary>
    [Fact]
    public async Task Xorcom_SaveFailureRestoresOldStation()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Service.ThrowOnSave = true;

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["sync:old:True", "save", "sync:old:False"], fixture.Service.Calls);
        Assert.Contains("SQL Save failed", fixture.Get<string?>("_validationMessage"));
    }

    /// <summary>Неуспешная очистка старой Xorcom запрещает SQL Save.</summary>
    [Fact]
    public async Task Xorcom_PreSaveFailureBlocksSqlWrite()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareXorcomStationChange();
        fixture.Service.FailDeletingXorcom = true;

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["sync:old:True"], fixture.Service.Calls);
        Assert.Empty(fixture.Service.SaveRequests);
        Assert.Contains("Данные не сохранены", fixture.Get<string?>("_validationMessage"));
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

    /// <summary>Ошибка SQL Delete после очистки Xorcom вызывает восстановление номера.</summary>
    [Fact]
    public async Task Xorcom_DeleteFailureRestoresOldStation()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareWrite();
        fixture.SetMember("_originalPhone", fixture.Model);
        fixture.SetMember("_originalStation", new PhoneStationLookup { Id = 1, XorcomName = "old" });
        fixture.SetMember("_selectedStation", new PhoneStationLookup { Id = 1, XorcomName = "old" });
        fixture.Service.ThrowOnDelete = true;

        await fixture.Invoke("DeleteConfirmedAsync");

        Assert.Equal(["read", "sync:old:True", "delete", "sync:old:False"], fixture.Service.Calls);
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
        var message = fixture.WaitForConfirmation();
        Assert.Equal(draft, message.Contains("Закрыть форму без сохранения изменений?", StringComparison.Ordinal));
        Assert.Equal(pendingSync, message.Contains("синхронизация АТС не завершена", StringComparison.Ordinal));

        fixture.RespondToConfirmation("Нет");
        await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(fixture.Get<bool>("_allowNavigation"));
        Assert.Equal(draft ? "draft" : string.Empty, fixture.Model.Notes);
        Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
        Assert.Empty(fixture.Service.Calls);
    }

    /// <summary>Реальный Delete всегда называет удаляемые данные; отказ не выполняет чтение или удаление.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Delete_ConfirmationAlwaysExplainsDeletion(bool draft, bool pendingSync)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft, pendingSync);

        var action = fixture.Invoke("DeleteAsync");
        var message = fixture.WaitForConfirmation();
        Assert.Contains("Удалить номер, состав его PBX-группы, связи с оборудованием и параллельные вызовы?", message);
        Assert.Equal(pendingSync, message.Contains("синхронизация АТС не завершена", StringComparison.Ordinal));

        fixture.RespondToConfirmation("Нет");
        await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(fixture.Service.Calls);
        Assert.False(fixture.Get<bool>("_deleted"));
        Assert.False(fixture.Get<bool>("_allowNavigation"));
        Assert.Equal(draft ? "draft" : string.Empty, fixture.Model.Notes);
        Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
    }

    /// <summary>Подтверждённый Delete с черновиком и pending выполняет удаление и закрывает форму.</summary>
    [Fact]
    public async Task Delete_ConfirmedDraftAndPendingDeletesPhone()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: true);

        var action = fixture.Invoke("DeleteAsync");
        _ = fixture.WaitForConfirmation();
        fixture.RespondToConfirmation("Да");
        await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["read", "delete"], fixture.Service.Calls);
        Assert.True(fixture.Get<bool>("_deleted"));
        Assert.True(fixture.Get<bool>("_allowNavigation"));
    }

    /// <summary>Refresh подтверждает только сброс черновика и сохраняет pending при загрузке.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Refresh_PreservesPendingAndOnlyConfirmsDraft(bool draft, bool pendingSync)
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft, pendingSync);
        if (!draft)
        {
            await fixture.Invoke("RefreshAsync").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, fixture.Service.LoadCalls);
            Assert.Equal("reloaded", fixture.Model.Number);
            Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
            fixture.AssertNoConfirmation();
            return;
        }

        var action = fixture.Invoke("RefreshAsync");
        var message = fixture.WaitForConfirmation();
        Assert.Contains("Отменить несохранённые изменения и загрузить запись повторно?", message);
        Assert.DoesNotContain("повтор из этой формы будет недоступен", message);

        fixture.RespondToConfirmation("Нет");
        await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, fixture.Service.LoadCalls);
        Assert.Equal("draft", fixture.Model.Notes);
        Assert.Equal(pendingSync ? 1 : 0, fixture.PendingCount);
    }

    /// <summary>Подтверждённый Refresh сбрасывает черновик, но не теряет возможность повтора АТС.</summary>
    [Fact]
    public async Task Refresh_ConfirmedDraftReloadsAndPreservesPending()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareCloseState(draft: true, pendingSync: true);

        var action = fixture.Invoke("RefreshAsync");
        _ = fixture.WaitForConfirmation();
        fixture.RespondToConfirmation("Да");
        await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, fixture.Service.LoadCalls);
        Assert.Equal("reloaded", fixture.Model.Number);
        Assert.Equal(1, fixture.PendingCount);
    }

    private sealed class TelFixture : IAsyncDisposable
    {
        private readonly BunitContext _context;
        private readonly IRenderedComponent<MudBlazor.MudDialogProvider> _dialogProvider;
        private readonly Tel _tel;

        internal TelFixture()
        {
            var db = new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => new ScriptedConnection(new Queue<ScriptEntry>()));
            Service = new DelayedPhoneFormService(db);
            _context = new BunitContext();
            _context.JSInterop.Mode = JSRuntimeMode.Loose;
            var services = _context.Services;
            services.AddMudServices();
            services.AddSingleton<PhoneFormService>(Service);
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
            _tel = _context.Render<Tel>().Instance;
            Set("_model", new Phone
            {
                Id = 1002, Number = "101", FullNumber = "101", PhoneTypeId = 1, StationId = 1, CustomerId = 1,
                EmployeeId = 4, AtsDescription = "Test", ServicesKind = 1, ViewInDirectory = 1, Connected = "Room",
            });
        }

        internal DelayedPhoneFormService Service { get; }
        internal Phone Model => Get<Phone>("_model");
        internal int PendingCount => Get<IReadOnlyList<PhoneSyncOperation>>("_pendingOperations").Count;

        internal void PrepareWrite()
        {
            Set("JS", new TestJSRuntime());
            Set("_loaded", true);
            Set("_loading", false);
            Set("_canEdit", true);
            Service.PersistedSnapshot = new PhoneDeleteSnapshot
            {
                Phone = Model, Station = new PhoneStationLookup { Id = 1, XorcomName = "old" },
            };
        }

        internal void PrepareCloseState(bool draft, bool pendingSync)
        {
            PrepareWrite();
            _tel.Id = Model.Id;
            Set("_loadedKey", (Model.Id, _tel.CopyId));
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
            Set("_originalPhone", original);
            var relations = Get<PhoneRelationsSnapshot>("_relations");
            Set("_originalRelations", relations.Clone());
            Set("_originalRelationsSnapshot", System.Text.Json.JsonSerializer.Serialize(relations));
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
                Id = Model.Id, Number = Model.Number, FullNumber = Model.FullNumber, PhoneTypeId = Model.PhoneTypeId,
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
            Set("_originalPhone", original);
            var station = new PhoneStationLookup { Id = stationId, XorcomName = stationId == 32 ? string.Empty : key };
            Set("_originalStation", station);
            Set("_selectedStation", station);
        }

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

        internal void AssertNoConfirmation() =>
            Assert.Empty(_dialogProvider.FindComponents<global::Clayzor.Lib.Web.Controls.Components.ConfirmDialog>());

        internal void SetMember(string name, object? value) => Set(name, value);

        internal T Get<T>(string name) => (T)Member(name).GetValue(_tel)!;

        internal Task Invoke(string name, params object?[] arguments) =>
            _context.Renderer.Dispatcher.InvokeAsync(() =>
                (Task)typeof(Tel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tel, arguments)!);

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
        internal bool ThrowOnDelete { get; set; }
        internal bool FailDeletingXorcom { get; set; }
        internal List<PhoneValidationIssue> ValidationIssues { get; } = [];
        internal string? SavedFullNumber { get; set; }
        internal PhoneDeleteSnapshot? PersistedSnapshot { get; set; }
        internal int LoadCalls { get; private set; }

        public override Task<PhoneFormData?> GetAsync(int id, bool copy = false)
        {
            LoadCalls++;
            Calls.Add("load");
            return Task.FromResult<PhoneFormData?>(new PhoneFormData
            {
                Phone = new Phone { Id = id, Number = "reloaded", FullNumber = "reloaded" }, CanEdit = true,
            });
        }

        internal TaskCompletionSource<PhoneCapabilities> EnqueueCapabilities()
        {
            var result = new TaskCompletionSource<PhoneCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
            _capabilities.Enqueue(result);
            return result;
        }

        public override Task<PhoneCapabilities> GetCapabilitiesAsync(int? stationId, int? phoneTypeId, int phoneId) =>
            _capabilities.Count > 0 ? _capabilities.Dequeue().Task : Task.FromResult(new PhoneCapabilities { HasPorts = stationId == 32 });

        public override Task<IReadOnlyList<PhoneValidationIssue>> ValidateServerAsync(PhoneSaveRequest request) =>
            Task.FromResult<IReadOnlyList<PhoneValidationIssue>>(ValidationIssues);

        public override Task<PhoneSaveResult> SaveAsync(PhoneSaveRequest request)
        {
            Calls.Add("save");
            if (ThrowOnSave) throw new InvalidOperationException("SQL Save failed");
            if (SavedFullNumber is not null) request.Phone.FullNumber = SavedFullNumber;
            SaveRequests.Add(request);
            return Task.FromResult(new PhoneSaveResult { PhoneId = request.Phone.Id, Saved = true });
        }

        public override Task<PhoneDeleteSnapshot?> GetDeleteSnapshotAsync(int id)
        {
            Calls.Add("read");
            return Task.FromResult(PersistedSnapshot);
        }

        public override Task DeleteAsync(int id)
        {
            Calls.Add("delete");
            if (ThrowOnDelete) throw new InvalidOperationException("SQL Delete failed");
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<PhoneSyncResult>> SynchronizeAsync(int phoneId, IEnumerable<PhoneSyncOperation> operations)
        {
            var results = operations.Select(operation =>
            {
                Calls.Add($"sync:{operation.Key}:{operation.Deleting}");
                var error = FailDeletingXorcom && operation.Kind == PhoneSyncKind.Xorcom && operation.Deleting ? "mock error" : null;
                return new PhoneSyncResult(operation, error);
            }).ToArray();
            return Task.FromResult<IReadOnlyList<PhoneSyncResult>>(results);
        }

        public override Task<int?> GetEmployeeOrganizationAsync(int employeeId) => Organizations[employeeId].Task;

        public override Task<string> GetAtsDescriptionAsync(int employeeId, bool upperCase) =>
            DescriptionDelay?.Task ?? Task.FromResult($"Employee{employeeId}");

        public override Task<(int Id, string Number)?> GetDefaultEmployeePhoneAsync(int employeeId) =>
            Task.FromResult<(int, string)?>((employeeId, $"10{employeeId}"));
    }

    private sealed class TestJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
