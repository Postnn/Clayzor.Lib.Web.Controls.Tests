using System.Security.Claims;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Редактор сохранённого периода публикует точную цель, закрытие делает её недействительной.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationEditorPresenceRequiresExistingPeriodAndLiveDialog(bool existing)
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var start = new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);
        var period = new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 55, FromUtc = start };
        if (existing)
            fixture.Service.StoredLocations.Add(period);
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenLocationDialogAsync", existing ? period : (EquipmentLocationPeriod?)null);
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dialog = provider.FindComponent<EquipmentLocationDialog>();
        var target = dialog.Instance.PresenceTarget;
        if (existing)
        {
            Assert.NotNull(target);
            Assert.Equal(ClayPresenceKey.Create("inventory", "equipment-location-period", 7, start.Ticks), target.Key);
            Assert.Single(dialog.FindComponents<ClayPresencePanel>());
        }
        else
        {
            Assert.Null(target);
            Assert.Empty(dialog.FindComponents<ClayPresencePanel>());
        }
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await pending.WaitAsync(TimeSpan.FromSeconds(3));

        if (target is not null)
        {
            var windows = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"));
            Assert.Null(await target.AuthorizeAsync(windows, default));
        }
    }
}

/// <summary>Проверяет доступ к точной сохранённой записи на стороне сервера.</summary>
public sealed class EquipmentChildPresenceAccessTests
{
    /// <summary>Отзыв роли останавливает опрос до запроса периода. Удаление периода отзывает присутствие.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PeriodAccessRechecksRoleAndStoredRow(bool hasRole, bool hasRow)
    {
        var start = new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc).AddMilliseconds(2);
        var entries = new List<ScriptEntry>
        {
            Script.Rows(Script.Row(("КодСотрудника", 4))),
            Script.Rows(Script.Row(("RoleId", hasRole ? (int)Роли.Специалист_по_инвентаризации_оборудования :
                (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))),
        };
        if (hasRole)
            entries.Add(hasRow ? Script.Rows(Script.Row(("Present", 1))) : Script.Rows());
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var service = new EquipmentFormService(db, new TestAuthenticationStateProvider());
        var subject = service.CreateResponsiblePresence(7, start, requireActive: true);

        var access = await EquipmentFormService.ReadChildPresenceAccessAsync(db, subject, "fixture", 4, default);

        Assert.Equal(hasRole && hasRow, access is { EmployeeId: 4, Activity: ClayPresenceActivity.Editing });
        var periods = connection.CommandLog.Select((sql, index) => (sql, index))
            .Where(entry => entry.sql == EquipmentChildPresenceQueries.ResponsiblePresenceRow).ToArray();
        if (!hasRole)
        {
            Assert.Empty(periods);
            return;
        }
        var index = Assert.Single(periods).index;
        var parameters = connection.ParameterLog[index];
        var sqlStart = EquipmentPeriodJournalWrite.NormalizeStartUtc(start);
        Assert.Equal(7, parameters["EquipmentId"]);
        Assert.Equal(sqlStart, parameters["FromUtc"]);
        Assert.Equal(true, parameters["RequireActive"]);
        Assert.Equal(ClayPresenceKey.Create("inventory", "equipment-responsible-period", 7, sqlStart.Ticks), subject.Key);
    }

    /// <summary>Опрос существующей SIM-услуги читает текущую принадлежность по двум идентификаторам.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimServicePresenceRequiresCurrentEquipmentMembership(bool belongsToEquipment)
    {
        DapperColumnMapper.Initialize();
        var scripts = new Queue<ScriptEntry>(
        [
            Script.Rows(Script.Row(("КодСотрудника", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))),
            Script.Rows(Script.Row(("ЕстьХарактеристикиSIM", 1))),
            belongsToEquipment ? Script.Rows(Script.Row(("Present", 1))) : Script.Rows(),
        ]);
        var connection = new ScriptedConnection(scripts, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var service = new EquipmentFormService(db, new TestAuthenticationStateProvider());
        var subject = service.CreateSimServicePresence(7, 21);
        Assert.NotNull(subject);

        var access = await EquipmentFormService.ReadChildPresenceAccessAsync(db, subject, "fixture", 4, default);

        Assert.Equal(belongsToEquipment, access is { EmployeeId: 4, Activity: ClayPresenceActivity.Editing });
        Assert.Equal(ClayPresenceKey.Create("inventory", "equipment-sim-service", 21), subject.Key);
        Assert.Equal(EquipmentChildPresenceQueries.SimServicePresenceRow, connection.CommandLog[^1]);
        Assert.Equal(2, connection.ParameterLog[^1].Count);
        Assert.Equal(7, connection.ParameterLog[^1]["EquipmentId"]);
        Assert.Equal(21, connection.ParameterLog[^1]["Id"]);
        Assert.Null(service.CreateSimServicePresence(7, 0));
    }
}
