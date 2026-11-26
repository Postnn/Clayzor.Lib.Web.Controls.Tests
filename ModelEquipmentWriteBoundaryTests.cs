using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет общую границу записи модели и шаблона без Razor и реальной БД.</summary>
public sealed class ModelEquipmentWriteBoundaryTests
{
    private const string Principal = @"DOMAIN\fixture";

    /// <summary>Неполный ввод останавливает прямое сохранение до первого запроса.</summary>
    [Theory]
    [InlineData(0, "", "", 0, "")]
    [InlineData(1, "Model", "ab", 0, "")]
    [InlineData(2, "Model", "ab", 0, "")]
    [InlineData(5, "Model", "", 1, "ab")]
    public async Task DirectSave_InvalidModelDoesNotWrite(int typeId, string name, string pn, int net, string netName)
    {
        var (db, connection) = Create([]);
        var input = new EquipmentModel { EquipmentTypeId = typeId, Name = name, PartNumber = pn, NetworkInterfaces = net, NetworkName = netName };
        await Assert.ThrowsAsync<InvalidOperationException>(() => input.SaveAsync(db, Principal, 4));
        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Отсутствующая, несовпадающая или неподтверждённая личность не приводит к DML.</summary>
    [Theory]
    [InlineData("unavailable")]
    [InlineData("mismatch")]
    [InlineData("anonymous")]
    public async Task DirectSave_RequiresFreshTrustedIdentity(string mode)
    {
        var identity = mode switch
        {
            "unavailable" => new[] { Script.Error(SqlExceptionFactory.Create(53, "fixture offline")) },
            "mismatch" => [Actor(9), Roles(Роли.Специалист_по_инвентаризации_оборудования)],
            _ => Array.Empty<ScriptEntry>(),
        };
        var (db, connection) = Create(mode == "anonymous" ? [] : [Type(5), Script.Scalar(0), .. identity]);
        var model = new EquipmentModel { EquipmentTypeId = 5, Name = "Valid" };

        if (mode == "unavailable")
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => model.SaveAsync(db, Principal, 4));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => model.SaveAsync(db, mode == "anonymous" ? null : Principal, 4));

        Assert.DoesNotContain(connection.CommandLog, IsWrite);
    }

    /// <summary>Отозванная роль не принимается для шаблона, прежнее право специалиста не заменяет роль инженера.</summary>
    [Fact]
    public async Task DirectTemplateSave_WrongRoleDoesNotWrite()
    {
        var (db, connection) = Create([Model(7, (int)ТипыОборудования.IP_телефон), Actor(),
            Roles(Роли.Специалист_по_инвентаризации_оборудования)]);
        var model = new EquipmentModel { Id = 7, PhoneTemplate = "cfg" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => model.SavePhoneTemplateAsync(db, Principal, 4, IpBaseline()));
        Assert.DoesNotContain(connection.CommandLog, IsWrite);
    }

    /// <summary>Сохранённый тип модели проверяется заново до записи шаблона.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectTemplateSave_ChangedTypeOrMissingModelDoesNotWrite(bool missing)
    {
        var (db, connection) = Create(missing ? [Script.Rows()] : [Model(7, 5)]);
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = (int)ТипыОборудования.IP_телефон, PhoneTemplate = "cfg" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.SavePhoneTemplateAsync(db, Principal, 4, IpBaseline()));
        Assert.DoesNotContain(connection.CommandLog, IsWrite);
    }

    /// <summary>Актуальные модель, тип, идентичность и роль разрешают штатный SQL INSERT или UPDATE шаблона.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSave_ValidActorAndModelWriteOnce(bool template)
    {
        var (db, connection) = Create(template
            ? [Model(7, (int)ТипыОборудования.IP_телефон), Actor(), Roles(Роли.Дежурный_инженер), Script.Scalar(1)]
            : [Type(5), Script.Scalar(0), Actor(), Roles(Роли.Специалист_по_инвентаризации_оборудования), Script.Scalar(42)]);

        if (template)
            await new EquipmentModel { Id = 7, PhoneTemplate = "cfg" }.SavePhoneTemplateAsync(db, Principal, 4, IpBaseline());
        else
            Assert.Equal(42, await new EquipmentModel { EquipmentTypeId = 5, Name = "Valid" }.SaveAsync(db, Principal, 4));

        Assert.Equal(template ? SQLQueries.UPDATE_МоделиОборудования_ШаблонТелефона : SQLQueries.INSERT_МоделиОборудования,
            Assert.Single(connection.CommandLog, IsWrite));
    }

    /// <summary>Смена типа используемой модели требует подтверждения именно переданного типа.</summary>
    [Fact]
    public async Task DirectSave_UnconfirmedUsedTypeChangeDoesNotWrite()
    {
        var (db, connection) = Create([Type(5), Model(7, 9), Script.Rows(Script.Row(("N", 1), ("Списанного", 0)))]);
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Valid" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.SaveAsync(db, Principal, 4, new EquipmentModelWriteBaseline(7, 9, null, null), confirmedTypeId: 8));
        Assert.DoesNotContain(connection.CommandLog, IsWrite);
    }

    /// <summary>Несуществующий тип и обнаруженный дубликат блокируют запись и в обход формы.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSave_InvalidTypeOrDuplicateDoesNotWrite(bool duplicate)
    {
        var (db, connection) = Create(duplicate ? [Type(5), Script.Scalar(1)] : [Script.Rows()]);
        var model = new EquipmentModel { EquipmentTypeId = 5, Name = "Valid" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => model.SaveAsync(db, Principal, 4));

        Assert.Contains(duplicate ? "уже существует" : "тип оборудования не найден", error.Message);
        Assert.DoesNotContain(connection.CommandLog, IsWrite);
    }

    private static (DbManager Db, ScriptedConnection Connection) Create(IEnumerable<ScriptEntry> entries)
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        return (new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection), connection);
    }

    internal static ScriptEntry Type(int id, byte maxNet = 0) =>
        Script.Rows(Script.Row(("КодТипаОборудования", id), ("ЕстьMACадрес", maxNet)));

    internal static ScriptEntry Model(int id, int typeId) =>
        Script.Rows(Script.Row(("КодМоделиОборудования", id), ("КодТипаОборудования", typeId)));

    internal static ScriptEntry Actor(int id = 4) => Script.Rows(Script.Row(("КодСотрудника", id)));

    internal static ScriptEntry Roles(params Роли[] roles) => Script.Rows(roles
        .Select(role => Script.Row(("RoleId", (int)role), ("PersonId", 0))).ToArray());

    private static EquipmentModelWriteBaseline IpBaseline() => new(7, (int)ТипыОборудования.IP_телефон, null, null);

    internal static bool IsWrite(string sql) => sql == SQLQueries.INSERT_МоделиОборудования ||
        sql == SQLQueries.UPDATE_МоделиОборудования || sql == SQLQueries.UPDATE_МоделиОборудования_ОчиститьШаблон ||
        sql == SQLQueries.UPDATE_МоделиОборудования_ШаблонТелефона;
}
