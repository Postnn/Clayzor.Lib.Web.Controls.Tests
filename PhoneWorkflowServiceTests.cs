using System.Security.Cryptography;
using System.Text;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет границу долговечного шага АТС с подменённым SQL.</summary>
public sealed class PhoneWorkflowServiceTests
{
    private static readonly PhoneSyncOperation Meridian = new(PhoneSyncKind.Meridian, "42-09", true);

    /// <summary>Внешняя команда выполняется между долговечной отметкой и подтверждением ответа.</summary>
    [Fact]
    public async Task StepCommitsDispatchBeforeExecutingAndConfirmsAfterTheReply()
    {
        var (service, script, transactions) = Create(Actor(), Role(), Actor(), Role(), Script.Scalar(1),
            Script.NonQuery(), Actor(), Role(), Script.Scalar(1));
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
        var dispatch = script.CommandLog.IndexOf(SQLQueries.UPDATE_ClayzorTelWorkflowStepDispatched);
        var ats = script.CommandLog.IndexOf(SQLQueries.SP_ТелефонСинхронизацияMeridian);
        var confirmation = script.CommandLog.IndexOf(SQLQueries.UPDATE_ClayzorTelWorkflowStepConfirmed);
        Assert.True(dispatch >= 0 && dispatch < ats && ats < confirmation);
        Assert.Equal(["begin", "commit", "begin", "commit"], transactions.TransactionLog);
        Assert.Equal(attemptId, script.ParameterLog[dispatch]["AttemptId"]);
        Assert.Equal(17, script.ParameterLog[dispatch]["PhoneId"]);
        Assert.Equal(2, script.ParameterLog[dispatch]["StepIndex"]);
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            """{"Kind":0,"Key":"42-09","Deleting":true}"""))).ToLowerInvariant();
        Assert.Equal(expectedHash, script.ParameterLog[dispatch]["Fingerprint"]);
        Assert.Equal(expectedHash, script.ParameterLog[confirmation]["Fingerprint"]);
        Assert.DoesNotContain("42-09", script.ParameterLog[dispatch].Values.OfType<string>());
    }

    /// <summary>Определённое сервером состояние шага не позволяет вторично отправить команду.</summary>
    [Theory]
    [InlineData(2, PhoneSyncPhase.Succeeded)]
    [InlineData(0, PhoneSyncPhase.Unknown)]
    public async Task ConfirmedOrBlockedStepDoesNotExecuteTheAtsProcedure(int state, PhoneSyncPhase expected)
    {
        var (service, script, _) = Create(Actor(), Role(), Actor(), Role(), Script.Scalar(state));

        var result = await service.SynchronizeWorkflowStepAsync(Guid.NewGuid(), "fixture", 4, 17, false, 0, Meridian, default);

        Assert.Equal(expected, result.Phase);
        Assert.False(result.CanRetry);
        Assert.DoesNotContain(SQLQueries.SP_ТелефонСинхронизацияMeridian, script.CommandLog);
        Assert.DoesNotContain(SQLQueries.UPDATE_ClayzorTelWorkflowStepConfirmed, script.CommandLog);
    }

    /// <summary>После отметки отправки любой отказ без подтверждённого успеха оставляет Unknown.</summary>
    [Fact]
    public async Task FailureAfterDispatchNeverConfirmsOrAllowsRetry()
    {
        var (service, script, _) = Create(Actor(), Role(), Actor(), Role(), Script.Scalar(1),
            Script.Error(new InvalidOperationException("Ответ процедуры не подтверждён.")));

        var result = await service.SynchronizeWorkflowStepAsync(Guid.NewGuid(), "fixture", 4, 17, false, 0, Meridian, default);

        Assert.Equal(PhoneSyncPhase.Unknown, result.Phase);
        Assert.False(result.CanRetry);
        Assert.Single(script.CommandLog.Where(command => command == SQLQueries.SP_ТелефонСинхронизацияMeridian));
        Assert.DoesNotContain(SQLQueries.UPDATE_ClayzorTelWorkflowStepConfirmed, script.CommandLog);
    }

    /// <summary>Отозванное право и отсутствие поддержки удаления проверяются до долговечного dispatch.</summary>
    [Fact]
    public async Task RevokedRoleAndMissingDeleteParameterDoNotDispatch()
    {
        var (noRole, denied, _) = Create(Actor(), Script.Rows());
        var deniedResult = await noRole.SynchronizeWorkflowStepAsync(Guid.NewGuid(), "fixture", 4, 17, false, 0, Meridian, default);
        Assert.Equal(PhoneSyncPhase.Rejected, deniedResult.Phase);
        Assert.DoesNotContain(SQLQueries.UPDATE_ClayzorTelWorkflowStepDispatched, denied.CommandLog);

        var (noDelete, catalog, _) = Create(Script.Scalar(0));
        var unsupported = await noDelete.SynchronizeWorkflowStepAsync(Guid.NewGuid(), "fixture", 4, 17, true, 0,
            new PhoneSyncOperation(PhoneSyncKind.Xorcom, "station", true), default);
        Assert.Equal(PhoneSyncPhase.Rejected, unsupported.Phase);
        Assert.Equal(SQLQueries.SELECT_ПроцедураИмеетПараметр, Assert.Single(catalog.CommandLog));
    }

    private static (PhoneFormService Service, ScriptedConnection Script, EquipmentAccountTransactionConnection Transactions)
        Create(params ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var connection = new EquipmentAccountTransactionConnection(script);
        return (new PhoneFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection)), script, connection);
    }

    private static ScriptEntry Actor() => Script.Rows(Script.Row(("КодСотрудника", 4)));

    private static ScriptEntry Role() => Script.Rows(Script.Row(
        ("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)));
}
