using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет границу шага АТС с подменённым SQL.</summary>
public sealed class PhoneWorkflowServiceTests
{
    private static readonly PhoneSyncOperation Meridian = new(PhoneSyncKind.Meridian, "42-09", true);

    /// <summary>Внешняя команда выполняется после подтверждения временной фазы.</summary>
    [Fact]
    public async Task StepUsesExistingProcedureAfterPreparingLocalState()
    {
        var (service, script) = Create(Actor(), Role(), Script.NonQuery());
        var attemptId = Guid.NewGuid();
        var prepared = false;

        var result = await service.SynchronizeWorkflowStepAsync(attemptId, "fixture", 4, 17, false, 2,
            Meridian, default, operation =>
            {
                Assert.Equal(Meridian, operation);
                Assert.Empty(script.CommandLog);
                prepared = true;
                return Task.CompletedTask;
            });

        Assert.True(prepared);
        Assert.Equal(PhoneSyncPhase.Succeeded, result.Phase);
        Assert.Single(script.CommandLog, sql => sql == SQLQueries.SP_ТелефонСинхронизацияMeridian);
        Assert.DoesNotContain(script.CommandLog, sql => sql.Contains("dbo.Clayzor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>После отметки отправки любой отказ без подтверждённого успеха оставляет Unknown.</summary>
    [Fact]
    public async Task FailureAfterDispatchNeverConfirmsOrAllowsRetry()
    {
        var (service, script) = Create(Actor(), Role(),
            Script.Error(new InvalidOperationException("Ответ процедуры не подтверждён.")));

        var result = await service.SynchronizeWorkflowStepAsync(Guid.NewGuid(), "fixture", 4, 17, false, 0, Meridian, default);

        Assert.Equal(PhoneSyncPhase.Unknown, result.Phase);
        Assert.False(result.CanRetry);
        Assert.Single(script.CommandLog.Where(command => command == SQLQueries.SP_ТелефонСинхронизацияMeridian));
    }

    /// <summary>Отозванное право и отсутствие поддержки удаления проверяются до долговечного dispatch.</summary>
    [Fact]
    public async Task RevokedRoleAndMissingDeleteParameterDoNotDispatch()
    {
        var (noRole, denied) = Create(Actor(), Script.Rows());
        var deniedResult = await noRole.SynchronizeWorkflowStepAsync(Guid.NewGuid(), "fixture", 4, 17, false, 0, Meridian, default);
        Assert.Equal(PhoneSyncPhase.Rejected, deniedResult.Phase);
        Assert.DoesNotContain(SQLQueries.SP_ТелефонСинхронизацияMeridian, denied.CommandLog);

        var (noDelete, catalog) = Create(Script.Scalar(0));
        var unsupported = await noDelete.SynchronizeWorkflowStepAsync(Guid.NewGuid(), "fixture", 4, 17, true, 0,
            new PhoneSyncOperation(PhoneSyncKind.Xorcom, "station", true), default);
        Assert.Equal(PhoneSyncPhase.Rejected, unsupported.Phase);
        Assert.Equal(SQLQueries.SELECT_ПроцедураИмеетПараметр, Assert.Single(catalog.CommandLog));
    }

    private static (PhoneFormService Service, ScriptedConnection Script)
        Create(params ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        return (new PhoneFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => script)), script);
    }

    private static ScriptEntry Actor() => Script.Rows(Script.Row(("КодСотрудника", 4)));

    private static ScriptEntry Role() => Script.Rows(Script.Row(
        ("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)));
}
