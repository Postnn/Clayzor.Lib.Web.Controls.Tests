using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки SIM, секретов и атомарного списания дубликата.</summary>
public sealed class EquipmentSimPersistenceTests
{
    /// <summary>Проверяются обязательный договор, цифровой номер и длины кодов.</summary>
    [Fact]
    public void Validate_ChecksRequiredFieldsAndCodes()
    {
        var sim = ValidSim();
        sim.Secrets.Pin = "12A4";
        sim.Secrets.Puk2 = "123";

        Assert.Equal("PIN должен содержать ровно 4 цифры.", sim.Validate());
        sim.Secrets.Pin = "1234";
        Assert.Equal("PUK2 должен содержать ровно 8 цифр.", sim.Validate());
        sim.Secrets.Puk2 = "12345678";
        Assert.Null(sim.Validate());
        sim.PhoneNumber = "+7900";
        Assert.Equal("Номер телефона должен содержать только цифры.", sim.Validate());
    }

    /// <summary>Общее чтение SIM не содержит PIN/PUK.</summary>
    [Fact]
    public async Task GetSimCards_DoesNotReadSecrets()
    {
        DapperColumnMapper.Initialize();
        var activated = new DateTime(2026, 11, 18, 8, 0, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("НомерТелефона", "79000000001"),
                ("От", activated), ("КодДоговора", 10), ("Договор", "Contract"),
                ("ЛицоПерсональныхДанных", "Person"), ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var cards = await service.GetSimCardsAsync(7);

        var card = Assert.Single(cards);
        Assert.Equal("79000000001", card.PhoneNumber);
        Assert.Equal(activated, card.ActivatedAtUtc);
        Assert.Equal("Contract", card.ContractName);
        Assert.DoesNotContain("PIN", connection.CommandLog[0]);
        Assert.DoesNotContain("PUK", connection.CommandLog[0]);
    }

    /// <summary>PIN/PUK читаются отдельно после проверки роли.</summary>
    [Fact]
    public async Task GetSimSecrets_ChecksRoleBeforeReading()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("PIN", "1234"), ("PIN2", "12345"),
                ("PUK", "12345678"), ("PUK2", "87654321"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var secrets = await service.GetSimSecretsAsync(7);

        Assert.Equal("1234", secrets.Pin);
        Assert.Equal("87654321", secrets.Puk2);
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.Contains("fn_ТекущиеРоли", connection.CommandLog[0]);
    }

    /// <summary>Подтверждённый дубликат списывается в той же транзакции, что и UPSERT.</summary>
    [Fact]
    public async Task SaveSimWithDuplicate_ComposesSingleTransaction()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        var sim = ValidSim();

        await service.SaveSimAsync(sim, 8);

        Assert.Contains("BEGIN TRANSACTION", connection.CommandLog[1]);
        Assert.Contains("UPDATE vwОборудование", connection.CommandLog[1]);
        Assert.Contains("UPDATE vwSIMКарты", connection.CommandLog[1]);
        Assert.Contains("COMMIT TRANSACTION", connection.CommandLog[1]);
        Assert.Contains("COALESCE", connection.CommandLog[1]);
    }

    /// <summary>Без дубликата не изменяется другая запись оборудования.</summary>
    [Fact]
    public async Task SaveSimWithoutDuplicate_DoesNotWriteOtherEquipment()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.SaveSimAsync(ValidSim());

        Assert.DoesNotContain("UPDATE vwОборудование", connection.CommandLog[1]);
        Assert.Contains("IF EXISTS", connection.CommandLog[1]);
    }

    /// <summary>Удаление затрагивает только представление SIM.</summary>
    [Fact]
    public async Task DeleteSim_DoesNotDeleteEquipment()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.DeleteSimAsync(7);

        Assert.Contains("DELETE vwSIMКарты", connection.CommandLog[1]);
        Assert.DoesNotContain("Оборудование SET", connection.CommandLog[1]);
    }

    /// <summary>Без роли 42 удаление SIM не выполняется.</summary>
    [Fact]
    public async Task DeleteSimWithoutRole_DoesNotWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteSimAsync(7));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("DELETE vwSIMКарты"));
    }

    private static EquipmentSimCard ValidSim() => new()
    {
        EquipmentId = 7,
        PhoneNumber = "79000000001",
        ActivatedAtUtc = new DateTime(2026, 11, 18, 8, 0, 0),
        ContractId = 10,
        Secrets = new EquipmentSimSecrets
        {
            Pin = "1234", Pin2 = "12345", Puk = "12345678", Puk2 = "87654321",
        },
    };
}
