using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки различения ANY, global и конкретного лица в UX-снимке Employee.</summary>
public sealed class Eq8EmployeeRoleScopeTests
{
    /// <summary>Локальная роль не становится глобальной, а null не означает любое лицо.</summary>
    [Fact]
    public void Snapshot_DistinguishesGlobalLocalAndUnknownPerson()
    {
        var employee = new Employee { Id = 4 };
        employee.SetRoleGrants([
            new EmployeeRoleGrant { RoleId = (int)Роли.Дежурный_инженер, PersonId = 1 },
            new EmployeeRoleGrant { RoleId = (int)Роли.Специалист_по_инвентаризации_оборудования, PersonId = 0 },
        ]);

        Assert.True(employee.HasRole(Роли.Дежурный_инженер));
        Assert.False(employee.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.Global));
        Assert.True(employee.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.PersonOrGlobal, 1));
        Assert.False(employee.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.PersonOrGlobal, 2));
        Assert.False(employee.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.PersonOrGlobal, null));
        Assert.True(employee.HasRole(Роли.Специалист_по_инвентаризации_оборудования,
            EmployeeRoleScope.PersonOrGlobal, null));
        Assert.False(employee.HasRole(Роли.Специалист_по_инвентаризации_оборудования,
            EmployeeRoleScope.PersonOnly, null));
    }

    /// <summary>Без загруженного снимка роль не считается доступной.</summary>
    [Fact]
    public void MissingSnapshot_FailsClosed()
    {
        var employee = new Employee { Id = 4 };

        Assert.False(employee.HasRole(Роли.Дежурный_инженер));
    }
}
