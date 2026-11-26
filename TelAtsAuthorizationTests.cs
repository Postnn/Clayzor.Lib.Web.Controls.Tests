using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет границу авторизации Tel перед фактическим вызовом процедуры АТС.</summary>
public sealed class TelAtsAuthorizationTests
{
    private const string Principal = @"DOMAIN\operator";

    /// <summary>Отсутствие личности, смена сотрудника или отзыв роли не отправляют команду.</summary>
    [Theory]
    [InlineData("anonymous")]
    [InlineData("mismatch")]
    [InlineData("revoked")]
    public async Task InvalidActor_PreventsMeridianDispatch(string mode)
    {
        var script = mode switch
        {
            "anonymous" => Array.Empty<ScriptEntry>(),
            "mismatch" => [Actor(99), Role()],
            _ => [Actor(), Script.Rows()],
        };
        var (service, connection) = CreateService(script);

        var result = Assert.Single(await service.SynchronizeTelAsync(mode == "anonymous" ? null : Principal, 4, 88,
            [new PhoneSyncOperation(PhoneSyncKind.Meridian, "00/14")], CancellationToken.None));

        Assert.False(result.Success);
        Assert.DoesNotContain(SQLQueries.SP_ТелефонСинхронизацияMeridian, connection.CommandLog);
        Assert.Equal(mode == "anonymous" ? 0 : 2, connection.CommandLog.Count);
    }

    /// <summary>Каждый dispatch перечитывает роль, даже если предыдущая команда в той же группе прошла.</summary>
    [Fact]
    public async Task RoleRevokedBetweenOperations_PreventsSecondDispatch()
    {
        var (service, connection) = CreateService([Actor(), Role(), Script.NonQuery(), Actor(), Script.Rows()]);
        var results = await service.SynchronizeTelAsync(Principal, 4, 88,
            [new PhoneSyncOperation(PhoneSyncKind.Meridian, "00/14"), new PhoneSyncOperation(PhoneSyncKind.Ccm, "91")],
            CancellationToken.None);

        Assert.Collection(results, first => Assert.True(first.Success), second => Assert.False(second.Success));
        Assert.Contains(SQLQueries.SP_ТелефонСинхронизацияMeridian, connection.CommandLog);
        Assert.DoesNotContain(SQLQueries.SP_PhoneSync_Ccm, connection.CommandLog);
        Assert.Equal(2, connection.CommandLog.Count(sql => sql == SQLQueries.SELECT_ТекущиеРолиДляСотрудника));
    }

    /// <summary>Запоздавшая команда Xorcom не пересинхронизирует номер после смены сохранённой станции.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XorcomRequiresMatchingPersistedStation(bool stationMatches)
    {
        const string originalStation = "старая станция";
        var entries = new List<ScriptEntry>
        {
            Script.Rows(Script.Row(("Value", 1))),
            Script.Rows(Script.Row(("КодТелефонногоНомера", 88), ("КодТелефоннойСтанции", 7))),
            Script.Rows(Script.Row(("КодТелефоннойСтанции", 7),
                ("XorcomName", stationMatches ? originalStation : "другая станция"))),
        };
        if (stationMatches)
            entries.AddRange([Actor(), Role(), Script.NonQuery()]);
        var (service, connection) = CreateService(entries);

        var result = Assert.Single(await service.SynchronizeTelAsync(Principal, 4, 88,
            [new PhoneSyncOperation(PhoneSyncKind.Xorcom, originalStation, Deleting: true)], CancellationToken.None));

        Assert.Equal(stationMatches, result.Success);
        Assert.Equal(stationMatches, connection.CommandLog.Contains(SQLQueries.SP_PhoneSync_Xorcom));
        if (!stationMatches)
            Assert.Contains("Сохранённая станция", result.Error);
    }

    private static (PhoneFormService Service, ScriptedConnection Connection) CreateService(IEnumerable<ScriptEntry> entries)
    {
        Clayzor.Lib.Entities.MedicalTests.DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        return (new PhoneFormService(new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection)), connection);
    }

    private static ScriptEntry Actor(int id = 4) => Script.Rows(Script.Row(("КодСотрудника", id)));
    private static ScriptEntry Role() => Script.Rows(Script.Row(
        ("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)));
}
