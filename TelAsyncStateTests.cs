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

        await fixture.Invoke("SaveAsync", true);

        Assert.Equal(["sync:old:True", "save", "sync:new:False"], fixture.Service.Calls);
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

    /// <summary>Удаление Xorcom выполняется до физического удаления записи.</summary>
    [Fact]
    public async Task Xorcom_DeleteRunsBeforeSqlDelete()
    {
        await using var fixture = new TelFixture();
        fixture.PrepareWrite();
        fixture.SetMember("_originalPhone", fixture.Model);
        fixture.SetMember("_originalStation", new PhoneStationLookup { Id = 1, XorcomName = "old" });
        fixture.SetMember("_selectedStation", new PhoneStationLookup { Id = 1, XorcomName = "old" });

        await fixture.Invoke("DeleteConfirmedAsync");

        Assert.Equal(["sync:old:True", "delete"], fixture.Service.Calls);
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

        Assert.Equal(["sync:old:True", "delete", "sync:old:False"], fixture.Service.Calls);
        Assert.False(fixture.Get<bool>("_deleted"));
    }

    private sealed class TelFixture : IAsyncDisposable
    {
        private readonly BunitContext _context;
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
            _tel = _context.Render<Tel>().Instance;
            Set("_model", new Phone
            {
                Id = 1002, Number = "101", FullNumber = "101", PhoneTypeId = 1, StationId = 1, CustomerId = 1,
                ServicesKind = 1, ViewInDirectory = 1, Connected = "Room",
            });
        }

        internal DelayedPhoneFormService Service { get; }
        internal Phone Model => Get<Phone>("_model");

        internal void PrepareWrite()
        {
            Set("JS", new TestJSRuntime());
            Set("_loaded", true);
            Set("_loading", false);
            Set("_canEdit", true);
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
            Model.StationId = 2;
            Set("_selectedStation", new PhoneStationLookup { Id = 2, XorcomName = "new" });
        }

        internal void SetMember(string name, object? value) => Set(name, value);

        internal T Get<T>(string name) => (T)Member(name).GetValue(_tel)!;

        internal Task Invoke(string name, params object?[] arguments) =>
            _context.Renderer.Dispatcher.InvokeAsync(() =>
                (Task)typeof(Tel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tel, arguments)!);

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

        internal TaskCompletionSource<PhoneCapabilities> EnqueueCapabilities()
        {
            var result = new TaskCompletionSource<PhoneCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
            _capabilities.Enqueue(result);
            return result;
        }

        public override Task<PhoneCapabilities> GetCapabilitiesAsync(int? stationId, int? phoneTypeId, int phoneId) =>
            _capabilities.Count > 0 ? _capabilities.Dequeue().Task : Task.FromResult(new PhoneCapabilities());

        public override Task<PhoneSaveResult> SaveAsync(PhoneSaveRequest request)
        {
            Calls.Add("save");
            if (ThrowOnSave) throw new InvalidOperationException("SQL Save failed");
            SaveRequests.Add(request);
            return Task.FromResult(new PhoneSaveResult { PhoneId = request.Phone.Id, Saved = true });
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
