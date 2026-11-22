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
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var returnDate = new DateTime(2026, 12, 1, 15, 30, 0);
        var draft = Draft();
        draft.Connected = "  srv-a  ";
        draft.Description = "  note  ";
        draft.ReturnDate = returnDate;

        await LocalNetworkPort.SaveAsync(CreateDb(connection), draft, draft.ChangedAtUtc);

        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal("srv-a", parameters["Connected"]);
        Assert.Equal("note", parameters["Description"]);
        Assert.Equal(returnDate.Date, parameters["ReturnDate"]);
        Assert.Equal(draft.ChangedAtUtc, parameters["ExpectedChangedAt"]);
        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("СрокВозврата = @ReturnDate", sql);
        Assert.Contains("UPDLOCK, HOLDLOCK", sql);
    }

    /// <summary>NULL из прямого вызова преобразуется в допустимые для NOT NULL колонок пустые строки.</summary>
    [Fact]
    public async Task Save_NullTexts_WritesEmptyStrings()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var draft = Draft();
        draft.Connected = null!;
        draft.Description = null!;

        await LocalNetworkPort.SaveAsync(CreateDb(connection), draft, draft.ChangedAtUtc);

        var parameters = Assert.Single(connection.ParameterLog);
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
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var draft = Draft();

        await LocalNetworkPort.SaveAsync(CreateDb(connection), draft, draft.ChangedAtUtc, switchToId: 11);

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("Подключено = ''", sql);
        Assert.Contains("КодСотрудника = NULL", sql);
        Assert.Contains("КодРозетки = NULL", sql);
        Assert.DoesNotContain("СрокВозврата", sql);
        Assert.Contains("КодРозетки IS NULL AND КодСотрудника IS NULL AND Подключено = ''", sql);
        Assert.Contains("fn_ЛокальнаяСеть_СоседниеПорты(@Id)", sql);
        Assert.Equal(11, Assert.Single(connection.ParameterLog)["TargetId"]);
    }

    /// <summary>Обмен выполняет три UPDATE в legacy-порядке и не меняет сроки возврата.</summary>
    [Fact]
    public async Task Save_Exchange_UsesThreeUpdatesWithoutReturnDate()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));

        await LocalNetworkPort.SaveAsync(CreateDb(connection), Draft(), DateTime.UtcNow, exchangeWithId: 11);

        var sql = Assert.Single(connection.CommandLog);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalNetworkPort.SaveAsync(
            CreateDb(connection), Draft(), DateTime.UtcNow, 11, 12));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalNetworkPort.SaveAsync(
            CreateDb(connection), Draft(), DateTime.UtcNow, switchToId: 10));
        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Статусы устаревшей записи, заказчика и цели сохраняют отдельную семантику.</summary>
    [Theory]
    [InlineData(1, "изменилась или недоступна")]
    [InlineData(2, "Не выбран заказчик")]
    [InlineData(3, "недопустим или уже занят")]
    public async Task Save_Status_FailsWithoutSuccess(int status, string message)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(status)]));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalNetworkPort.SaveAsync(CreateDb(connection), Draft(), DateTime.UtcNow));
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
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([entry]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalNetworkPort.SaveAsync(CreateDb(connection), Draft(), DateTime.UtcNow));
    }

    /// <summary>Ошибка соединения записи пробрасывается.</summary>
    [Fact]
    public async Task Save_Connectivity_Throws()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry { Exception = SqlExceptionFactory.Create(53, "connection error") },
        ]));
        await Assert.ThrowsAsync<SqlException>(() =>
            LocalNetworkPort.SaveAsync(CreateDb(connection), Draft(), DateTime.UtcNow));
    }

    /// <summary>Текущий сотрудник требует совпадение Windows/SQL principal.</summary>
    [Fact]
    public async Task CurrentEmployee_RequiresMatchingPrincipal()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."),
                ("Employee", "Test"), ("КодЛицаЗаказчика", 1))),
        ]));
        var employee = await Employee.GetCurrentAsync(CreateDb(connection), "DOMAIN\\user");
        Assert.Equal(4, employee!.Id);
        Assert.Equal(1, employee.OrganizationId);
        Assert.Contains("SID = SUSER_SID()", connection.CommandLog[0]);
        Assert.Contains("@WindowsPrincipal = SUSER_SNAME()", connection.CommandLog[0]);
        Assert.Equal("DOMAIN\\user", connection.ParameterLog[0]["WindowsPrincipal"]);

        var missing = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        Assert.Null(await Employee.GetCurrentAsync(CreateDb(missing), "DOMAIN\\other"));
        Assert.Null(await Employee.GetCurrentAsync(CreateDb(new ScriptedConnection(new Queue<ScriptEntry>())), null));
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
}
