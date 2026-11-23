using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки фактической ModelId/Type и MAC-нормализации EQ8-13.</summary>
public sealed class Eq8ServerGuardsEquipmentTests
{
    /// <summary>Тип A не может записать модель B ни в обычном, ни в phone-diff path.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelTypeMismatch_RejectedBeforeDml(bool phoneDiff)
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(ModelTypeRow(actualTypeId: 2, macCount: 0)), Script.NonQuery(),
        ]));
        var service = CreateService(connection);
        var equipment = new Equipment { Id = 7, ModelId = 5, EquipmentTypeId = 1, OwnerId = 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(service, equipment, phoneDiff));

        Assert.Equal(2, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Разрешённые разделители удаляются после whitelist и до Dapper size12 в обоих path.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidMacs_NormalizedBeforeActualParameters(bool phoneDiff)
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(ModelTypeRow(actualTypeId: 1, macCount: 3)), Script.NonQuery(),
        ]));
        var service = CreateService(connection);
        var equipment = new Equipment
        {
            Id = 7,
            ModelId = 5,
            EquipmentTypeId = 1,
            OwnerId = 1,
            MacAddress = "00:11:22:33:44:55",
            MacAddress2 = "66-77-88-99-AA-BB",
            MacAddressIlo = "CC.DD.EE.FF.00.11",
        };

        await Save(service, equipment, phoneDiff);

        var parameters = connection.ParameterLog[2];
        Assert.Equal("001122334455", parameters["MACадрес"]);
        Assert.Equal("66778899AABB", parameters["MACадрес2"]);
        Assert.Equal("CCDDEEFF0011", parameters["MACадресILO"]);
        Assert.Equal(parameters["MACадрес"], equipment.MacAddress);
        Assert.Equal(parameters["MACадрес2"], equipment.MacAddress2);
        Assert.Equal(parameters["MACадресILO"], equipment.MacAddressIlo);
    }

    /// <summary>Пустые optional MAC остаются пустыми без substring/truncation.</summary>
    [Fact]
    public async Task EmptyOptionalMacs_NormalizeToEmptyParameters()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(ModelTypeRow(actualTypeId: 1, macCount: 1)), Script.NonQuery(),
        ]));
        var equipment = new Equipment
        {
            Id = 7,
            ModelId = 5,
            EquipmentTypeId = 1,
            OwnerId = 1,
            MacAddress = "00:11:22:33:44:55",
            MacAddress2 = null,
            MacAddressIlo = " ",
        };

        await CreateService(connection).UpdateAsync(equipment);

        var parameters = connection.ParameterLog[2];
        Assert.Equal("001122334455", parameters["MACадрес"]);
        Assert.Equal(string.Empty, parameters["MACадрес2"]);
        Assert.Equal(string.Empty, parameters["MACадресILO"]);
    }

    /// <summary>Произвольные nonhex не удаляются: запись отклоняется до DML.</summary>
    [Fact]
    public async Task InvalidMac_NotSanitizedOrWritten()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(ModelTypeRow(actualTypeId: 1, macCount: 1)),
        ]));
        var equipment = new Equipment
        {
            Id = 7,
            ModelId = 5,
            EquipmentTypeId = 1,
            OwnerId = 1,
            MacAddress = "GG:11:22:33:44:55",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(connection).UpdateAsync(equipment));

        Assert.Equal("GG:11:22:33:44:55", equipment.MacAddress);
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
    }

    private static Task Save(EquipmentFormService service, Equipment equipment, bool phoneDiff) =>
        phoneDiff
            ? service.UpdateWithPhonesAsync(equipment, [], [new EquipmentPhoneLink { PhoneId = 11, EquipmentId = 7 }])
            : service.UpdateAsync(equipment);

    private static Dictionary<string, object?> ModelTypeRow(int actualTypeId, byte macCount) =>
        Script.Row(("КодТипаОборудования", actualTypeId), ("SNобязателен", (byte)0), ("ЕстьMACадрес", macCount));

    private static EquipmentFormService CreateService(ScriptedConnection connection) =>
        new(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());
}
