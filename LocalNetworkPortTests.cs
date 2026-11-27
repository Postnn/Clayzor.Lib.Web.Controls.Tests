using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки чтения, валидации и атомарной записи порта локальной сети.</summary>
public sealed class LocalNetworkPortTests
{

    /// <summary>Чтение сохраняет nullable-поля, исходный текст, автора и UTC-дату.</summary>
    [Fact]
    public async Task Get_MapsPortWithoutNullSubstitution()
    {
        DapperColumnMapper.Initialize();
        var changed = new DateTime(2026, 11, 21, 10, 0, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодЛокальнойСети", 10), ("КодОборудования", 8), ("Порт", (byte)2), ("PoE", (byte)1),
                ("КодРозетки", null), ("КодЛицаЗаказчика", 1), ("КодСотрудника", null),
                ("Подключено", "  srv-a  "), ("Примечания", " note "), ("СрокВозврата", null),
                ("Изменил", 4), ("Сотрудник", "Тестов А."), ("Изменено", changed))),
        ]));

        var port = await LocalNetworkPort.GetAsync(CreateDb(connection), 10);

        Assert.NotNull(port);
        Assert.Equal(10, port.Id);
        Assert.Equal(2, port.Port);
        Assert.Equal(1, port.Poe);
        Assert.Null(port.SocketId);
        Assert.Null(port.EmployeeId);
        Assert.Null(port.ReturnDate);
        Assert.Equal("  srv-a  ", port.Connected);
        Assert.Equal(" note ", port.Description);
        Assert.Equal("Тестов А.", port.ChangedByName);
        Assert.Equal(changed, port.ChangedAtUtc);
    }

    /// <summary>Некорректный ID не обращается к БД.</summary>
    [Fact]
    public async Task Get_InvalidId_DoesNotQuery()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>());
        Assert.Null(await LocalNetworkPort.GetAsync(CreateDb(connection), 0));
        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Legacy-валидация заказчика и розетки не подменяет «Подключено» примечанием.</summary>
    [Fact]
    public void Validate_UsesCustomerAndConnectedNotDescription()
    {
        Assert.Equal("Не выбран заказчик.", new LocalNetworkPort().Validate());
        var port = new LocalNetworkPort { PersonCustomerId = 1, SocketId = 5, Description = "not connected" };
        Assert.Equal("Вы указали розетку. В этом случае необходимо указать сотрудника или что подключено.", port.Validate());
        port.Connected = "srv-a";
        Assert.Null(port.Validate());
    }

    /// <summary>Обычная запись обрезает тексты только при сохранении и передаёт календарную дату.</summary>
    [Fact]
    public async Task Save_Normal_TrimsDraftAndWritesNullableDate()
    {
        var returnDate = new DateTime(2026, 12, 1, 15, 30, 0);
        var draft = Draft();
        draft.Connected = "  srv-a  ";
        draft.Description = "  note  ";
        draft.ReturnDate = returnDate;

        var (connection, db) = CreateSaveDb(draft, Script.Scalar(0));
        await LocalNetworkPort.SaveAsync(db, "DOMAIN\\user", 4, draft, draft.ChangedAtUtc);

        var parameters = SaveParameters(connection);
        Assert.Equal("srv-a", parameters["Connected"]);
        Assert.Equal("note", parameters["Description"]);
        Assert.Equal(returnDate.Date, parameters["ReturnDate"]);
        Assert.Equal(draft.ChangedAtUtc, parameters["ExpectedChangedAt"]);
        var sql = SaveSql(connection);
        Assert.Contains("СрокВозврата = @ReturnDate", sql);
        Assert.Contains("UPDLOCK, HOLDLOCK", sql);
    }

    /// <summary>NULL из прямого вызова преобразуется в допустимые для NOT NULL колонок пустые строки.</summary>
    [Fact]
    public async Task Save_NullTexts_WritesEmptyStrings()
    {
        var draft = Draft();
        draft.Connected = null!;
        draft.Description = null!;

        var (connection, db) = CreateSaveDb(draft, Script.Scalar(0));
        await LocalNetworkPort.SaveAsync(db, "DOMAIN\\user", 4, draft, draft.ChangedAtUtc);

        var parameters = SaveParameters(connection);
        Assert.Equal(string.Empty, parameters["Connected"]);
        Assert.Equal(string.Empty, parameters["Description"]);
    }

    /// <summary>Офис ищется только для последнего расположения, а не для более старой записи.</summary>
    [Fact]
    public void OfficeQuery_SelectsLatestLocationBeforeOfficeAncestor()
    {
        var sql = SQLQueries.SELECT_ОфисПоКодуОборудования;

        Assert.Contains("@LatestLocationId", sql);
        Assert.Contains("ORDER BY От DESC", sql);
        Assert.Contains("L.КодРасположения = @LatestLocationId", sql);
    }

    /// <summary>Переключение очищает источник, переносит черновик в цель и не меняет сроки возврата.</summary>
    [Fact]
    public async Task Save_Switch_UsesLegacyFieldMapWithoutReturnDate()
    {
        var draft = Draft();
        var targetVersion = draft.ChangedAtUtc.AddHours(2);
        var (connection, db) = CreateSaveDb(draft, Script.Scalar(0), targetVersion);

        await LocalNetworkPort.SaveAsync(db, "DOMAIN\\user", 4, draft, draft.ChangedAtUtc, switchToId: 11);

        var sql = SaveSql(connection);
        Assert.Contains("Подключено = ''", sql);
        Assert.Contains("КодСотрудника = NULL", sql);
        Assert.Contains("КодРозетки = NULL", sql);
        Assert.DoesNotContain("СрокВозврата", sql);
        Assert.Contains("КодРозетки IS NULL AND КодСотрудника IS NULL AND Подключено = ''", sql);
        Assert.Contains("fn_ЛокальнаяСеть_СоседниеПорты(@Id)", sql);
        Assert.Equal(11, SaveParameters(connection)["TargetId"]);
        Assert.Equal(targetVersion, SaveParameters(connection)["ExpectedTargetChangedAt"]);
    }

    /// <summary>Обмен выполняет три UPDATE в legacy-порядке и не меняет сроки возврата.</summary>
    [Fact]
    public async Task Save_Exchange_UsesThreeUpdatesWithoutReturnDate()
    {
        var draft = Draft();
        var (connection, db) = CreateSaveDb(draft, Script.Scalar(0), draft.ChangedAtUtc.AddHours(2));

        await LocalNetworkPort.SaveAsync(db, "DOMAIN\\user", 4, draft, draft.ChangedAtUtc, exchangeWithId: 11);

        var sql = SaveSql(connection);
        Assert.Equal(3, sql.Split("UPDATE vwЛокальнаяСеть").Length - 1);
        Assert.DoesNotContain("СрокВозврата", sql);
        Assert.True(sql.IndexOf("КодРозетки = NULL", StringComparison.Ordinal) <
                    sql.IndexOf("КодРозетки = @TargetSocketId", StringComparison.Ordinal));
    }

    /// <summary>Оба режима, собственный ID и неизвестный ID отклоняются до SQL.</summary>
    [Fact]
    public async Task Save_InvalidSelection_DoesNotWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>());
        await Assert.ThrowsAsync<LocalNetworkPortRejectedException>(() => LocalNetworkPort.SaveAsync(
            CreateDb(connection), "DOMAIN\\user", 4, Draft(), DateTime.UtcNow, 11, 12));
        await Assert.ThrowsAsync<LocalNetworkPortRejectedException>(() => LocalNetworkPort.SaveAsync(
            CreateDb(connection), "DOMAIN\\user", 4, Draft(), DateTime.UtcNow, switchToId: 10));
        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Статусы устаревшей записи, заказчика и цели сохраняют отдельную семантику.</summary>
    [Theory]
    [InlineData(1, "изменилась или недоступна")]
    [InlineData(2, "Не выбран заказчик")]
    [InlineData(3, "недопустим или уже занят")]
    public async Task Save_Status_FailsWithoutSuccess(int status, string message)
    {
        var draft = Draft();
        var (_, db) = CreateSaveDb(draft, Script.Scalar(status));
        var ex = await Assert.ThrowsAsync<LocalNetworkPortRejectedException>(() =>
            LocalNetworkPort.SaveAsync(db, "DOMAIN\\user", 4, draft, draft.ChangedAtUtc));
        Assert.Contains(message, ex.Message);
    }

    /// <summary>Connectivity, no-row и неизвестный статус не становятся успешной записью.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task Save_MissingOrUnknownStatus_FailsClosed(int? status)
    {
        var entry = status is int value ? Script.Scalar(value) : new ScriptEntry();
        var draft = Draft();
        var (_, db) = CreateSaveDb(draft, entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalNetworkPort.SaveAsync(db, "DOMAIN\\user", 4, draft, draft.ChangedAtUtc));
    }

    /// <summary>Ошибка соединения записи пробрасывается.</summary>
    [Fact]
    public async Task Save_Connectivity_Throws()
    {
        var draft = Draft();
        var (_, db) = CreateSaveDb(draft, Script.Error(SqlExceptionFactory.Create(53, "connection error")));
        await Assert.ThrowsAsync<SqlException>(() =>
            LocalNetworkPort.SaveAsync(db, "DOMAIN\\user", 4, draft, draft.ChangedAtUtc));
    }


    /// <summary>Текущий сотрудник требует совпадение Windows/SQL principal.</summary>
    [Fact]
    public async Task CurrentEmployee_RequiresMatchingPrincipal()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
                ("Employee", "Test"), ("КодЛицаЗаказчика", 1))),
        ]), emulateEquipmentIdentity: false);
        var employee = await Employee.GetCurrentAsync(CreateDb(connection), "DOMAIN\\user");
        Assert.Equal(4, employee!.Id);
        Assert.Equal(1, employee.OrganizationId);
        Assert.Contains("SID = SUSER_SID()", connection.CommandLog[0]);
        Assert.Contains("@WindowsPrincipal = SUSER_SNAME()", connection.CommandLog[0]);
        Assert.Equal("DOMAIN\\user", connection.ParameterLog[0]["WindowsPrincipal"]);

        var missing = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]), emulateEquipmentIdentity: false);
        Assert.Null(await Employee.GetCurrentAsync(CreateDb(missing), "DOMAIN\\other"));
        Assert.Null(await Employee.GetCurrentAsync(CreateDb(new ScriptedConnection(new Queue<ScriptEntry>(),
            emulateEquipmentIdentity: false)), null));
    }

    /// <summary>PoE вызывает подтверждённый ID с legacy timeout 120 секунд.</summary>
    [Fact]
    public async Task Poe_UsesCheckedIdAndLegacyTimeout()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));

        await LocalNetworkPort.ResetPoeAsync(CreateDb(connection), 10);

        Assert.Equal(120, Assert.Single(connection.TimeoutLog));
        Assert.Equal(10, Assert.Single(connection.ParameterLog)["КодЛокальнойСети"]);
        Assert.Contains("PoE = 1", connection.CommandLog[0]);
        Assert.Contains("sp_ResetPoEport", connection.CommandLog[0]);
    }

    private static LocalNetworkPort Draft() => new()
    {
        Id = 10,
        EquipmentId = 8,
        PersonCustomerId = 1,
        EmployeeId = 4,
        SocketId = 5,
        Connected = "srv-a",
        Description = "note",
        ReturnDate = new DateTime(2026, 12, 1),
        ChangedAtUtc = new DateTime(2026, 11, 21, 10, 0, 0),
    };

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);

    private static (ScriptedConnection Connection, DbManager Db) CreateSaveDb(
        LocalNetworkPort draft, ScriptEntry result, DateTime? targetVersion = null)
    {
        DapperColumnMapper.Initialize();
        var employee = Script.Rows(Script.Row(("КодСотрудника", 4)));
        var entries = new Queue<ScriptEntry>([employee,
            Script.Rows(Script.Row(("КодЛокальнойСети", draft.Id), ("Изменено", draft.ChangedAtUtc)))]);
        if (targetVersion is { } target)
            entries.Enqueue(Script.Rows(Script.Row(("КодЛокальнойСети", 11), ("Изменено", target))));
        entries.Enqueue(employee);
        entries.Enqueue(result);
        var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false) { AllowTransactions = true };
        return (connection, CreateDb(connection));
    }

    private static Dictionary<string, object?> SaveParameters(ScriptedConnection connection) =>
        Assert.Single(connection.ParameterLog, parameters => parameters.ContainsKey("ExpectedChangedAt"));

    private static string SaveSql(ScriptedConnection connection) =>
        Assert.Single(connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть", StringComparison.Ordinal));
}
