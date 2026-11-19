using System.Reflection;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.DBSelect.Phones;
using Kesco.App.Web.Inventory.Components.Pages.Telefon;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки версионирования зависимых асинхронных операций формы Tel.</summary>
public sealed class TelAsyncStateTests
{
    /// <summary>Поздний ответ сотрудника A не перезаписывает данные сотрудника B.</summary>
    [Fact]
    public async Task Employee_LatePreviousResponseIsIgnored()
    {
        using var fixture = new TelFixture();
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
        using var fixture = new TelFixture();
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
        using var fixture = new TelFixture();
        fixture.Model.EmployeeId = 4;
        fixture.Service.DescriptionDelay = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = fixture.Invoke("RefreshAutomaticAtsDescriptionAsync", true);
        _ = fixture.Invoke("OnAtsDescriptionChanged", "Manual");
        fixture.Service.DescriptionDelay.SetResult("Automatic");
        await refresh;

        Assert.Equal("Manual", fixture.Model.AtsDescription);
    }

    private sealed class TelFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Tel _tel = new();

        internal TelFixture()
        {
            var db = new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => new ScriptedConnection(new Queue<ScriptEntry>()));
            Service = new DelayedPhoneFormService(db);
            var services = new ServiceCollection();
            services.AddKeyedSingleton("phoneGroup", new PhoneDirectorySelectDataSource(db) { Mode = PhoneDirectoryMode.Group });
            services.AddKeyedSingleton("drivenBy", new PhoneDirectorySelectDataSource(db) { Mode = PhoneDirectoryMode.DrivenBy });
            services.AddKeyedSingleton("displayedFrom", new PhoneDirectorySelectDataSource(db) { Mode = PhoneDirectoryMode.DisplayedFrom });
            _provider = services.BuildServiceProvider();
            Set("Service", Service);
            Set("TypeSource", new PhoneTypeSelectDataSource(db));
            Set("StationSource", new PhoneStationSelectDataSource(db));
            Set("EquipmentSource", new PhoneEquipmentSelectDataSource(db));
            Set("SocketSource", new PhoneSocketSelectDataSource(db));
            Set("Configuration", new ConfigurationBuilder().Build());
            Set("Services", _provider);
            Set("_model", new Phone
            {
                Id = 1002, Number = "101", FullNumber = "101", PhoneTypeId = 1, StationId = 1,
                ServicesKind = 1, ViewInDirectory = 1, Connected = "Room",
            });
        }

        internal DelayedPhoneFormService Service { get; }
        internal Phone Model => Get<Phone>("_model");

        internal T Get<T>(string name) => (T)Member(name).GetValue(_tel)!;

        internal Task Invoke(string name, params object?[] arguments) =>
            (Task)typeof(Tel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tel, arguments)!;

        private void Set(string name, object? value) => Member(name).SetValue(_tel, value);

        private static MemberAccessor Member(string name) => new(name);

        public void Dispose()
        {
            _tel.Dispose();
            _provider.Dispose();
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
        internal Dictionary<int, TaskCompletionSource<int?>> Organizations { get; } = new()
        {
            [1] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [2] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        internal TaskCompletionSource<string>? DescriptionDelay { get; set; }

        public override Task<int?> GetEmployeeOrganizationAsync(int employeeId) => Organizations[employeeId].Task;

        public override Task<string> GetAtsDescriptionAsync(int employeeId, bool upperCase) =>
            DescriptionDelay?.Task ?? Task.FromResult($"Employee{employeeId}");

        public override Task<(int Id, string Number)?> GetDefaultEmployeePhoneAsync(int employeeId) =>
            Task.FromResult<(int, string)?>((employeeId, $"10{employeeId}"));
    }
}
