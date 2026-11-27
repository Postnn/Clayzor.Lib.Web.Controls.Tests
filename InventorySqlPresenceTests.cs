using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет идентичность, аренду и контракт общего хранилища присутствия Inventory.</summary>
public sealed class InventorySqlPresenceTests
{
    [Fact]
    public void SharedRegistrationIsNotReplacedByTheDefaultLocalRegistry()
    {
        var services = new ServiceCollection();
        services.AddScoped<IClayPresenceRegistry>(_ => Registry(Connection()));
        services.AddClayEntityPresence();
        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IClayPresenceRegistry));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<InventorySqlPresenceRegistry>(scope.ServiceProvider.GetRequiredService<IClayPresenceRegistry>());
    }

    [Fact]
    public void EntityHashRetainsTypeAndLosslessCanonicalParts()
    {
        var first = ClayPresenceKey.Create("inventory", "equipment-model", 19);
        var same = ClayPresenceKey.Create("inventory", "equipment-model", 19);
        Assert.Equal(InventorySqlPresenceRegistry.EntityHash(first), InventorySqlPresenceRegistry.EntityHash(same));
        Assert.NotEqual(InventorySqlPresenceRegistry.EntityHash(first),
            InventorySqlPresenceRegistry.EntityHash(ClayPresenceKey.Create("inventory", "equipment", 19)));
        Assert.NotEqual(InventorySqlPresenceRegistry.EntityHash(first),
            InventorySqlPresenceRegistry.EntityHash(ClayPresenceKey.Create("inventory", "equipment-model", "19")));
        Assert.NotEqual(InventorySqlPresenceRegistry.EntityHash(ClayPresenceKey.Create("inventory", "pair", "ab", "c")),
            InventorySqlPresenceRegistry.EntityHash(ClayPresenceKey.Create("inventory", "pair", "a", "bc")));
    }

    [Fact]
    public async Task SeparateClientsSendSameEntityAndRemoveOnlyTheirOwnLease()
    {
        var key = ClayPresenceKey.Create("inventory", "equipment-model", 19);
        var alice = new ClayPresenceAccess(4, "Алиса", ClayPresenceActivity.Editing);
        var boris = new ClayPresenceAccess(7, "Борис", ClayPresenceActivity.Viewing);
        var connectionA = Connection(Rows(alice), Rows(boris, alice), Script.NonQuery());
        var connectionB = Connection(Rows(alice, boris), Rows(), Rows(boris));
        using var first = Registry(connectionA);
        using var second = Registry(connectionB);

        var a = Assert.IsType<ClayPresenceSnapshot>(await first.HeartbeatAsync(key, alice, null, default));
        var b = Assert.IsType<ClayPresenceSnapshot>(await second.HeartbeatAsync(key, boris, null, default));
        Assert.Empty(a.Participants!);
        Assert.Equal(alice, Assert.Single(b.Participants!));
        Assert.NotEqual(a.Lease, b.Lease);
        var renewed = Assert.IsType<ClayPresenceSnapshot>(await first.HeartbeatAsync(key, alice, a.Lease, default));
        Assert.Equal(a.Lease, renewed.Lease);
        Assert.Equal(boris, Assert.Single(renewed.Participants!));

        await first.LeaveAsync(a.Lease);
        Assert.Null(await second.HeartbeatAsync(key, boris, b.Lease, default));
        var fresh = Assert.IsType<ClayPresenceSnapshot>(await second.HeartbeatAsync(key, boris, null, default));
        Assert.NotEqual(b.Lease, fresh.Lease);

        Assert.Equal(new[] { InventoryPresenceData.HeartbeatProcedure, InventoryPresenceData.HeartbeatProcedure,
            InventoryPresenceData.LeaveProcedure }, connectionA.CommandLog);
        var firstCall = connectionA.ParameterLog[0];
        var secondCall = connectionB.ParameterLog[0];
        Assert.Equal(Assert.IsType<byte[]>(firstCall["EntityHash"]), Assert.IsType<byte[]>(secondCall["EntityHash"]));
        Assert.Equal(alice.EmployeeId, firstCall["EmployeeId"]);
        Assert.Equal(boris.EmployeeId, secondCall["EmployeeId"]);
        Assert.Equal(45000, firstCall["LifetimeMilliseconds"]);
        Assert.Equal(false, firstCall["Renew"]);
        Assert.Equal(true, connectionA.ParameterLog[1]["Renew"]);
        Assert.Equal(a.Lease, Assert.Single(connectionA.ParameterLog[2]).Value);
        Assert.Equal(b.Lease, connectionB.ParameterLog[1]["LeaseId"]);
        Assert.Equal(fresh.Lease, connectionB.ParameterLog[2]["LeaseId"]);
    }

    [Fact]
    public async Task UnavailableSqlDoesNotBecomeAnEmptyList()
    {
        var connection = Connection(Script.Error(SqlExceptionFactory.Create(2812, "fixture procedure unavailable")));
        using var registry = Registry(connection);
        await Assert.ThrowsAsync<SqlException>(() => registry.HeartbeatAsync(
            ClayPresenceKey.Create("inventory", "equipment", 19),
            new ClayPresenceAccess(4, "Алиса", ClayPresenceActivity.Editing), null, default).AsTask());
    }

    [Fact]
    public async Task CorruptSharedSnapshotCannotDisplayAnEmployeeTwice()
    {
        var alice = new ClayPresenceAccess(4, "Алиса", ClayPresenceActivity.Editing);
        var boris = new ClayPresenceAccess(7, "Борис", ClayPresenceActivity.Viewing);
        using var registry = Registry(Connection(Rows(alice, boris, boris)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.HeartbeatAsync(
            ClayPresenceKey.Create("inventory", "equipment", 19), alice, null, default).AsTask());
    }

    private static ScriptEntry Rows(params ClayPresenceAccess[] employees) => Script.Rows(employees.Select(person =>
        Script.Row(("EmployeeId", person.EmployeeId), ("DisplayName", person.DisplayName), ("Activity", (byte)person.Activity))).ToArray());

    private static ScriptedConnection Connection(params ScriptEntry[] entries) =>
        new(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);

    private static InventorySqlPresenceRegistry Registry(ScriptedConnection connection) =>
        new(new DbManager("Server=fixture", null, () => connection), new ClayPresenceOptions());
}
