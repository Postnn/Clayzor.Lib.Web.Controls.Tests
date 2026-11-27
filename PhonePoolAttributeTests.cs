using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.PhonePoolAttribute;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Контракт составного ключа, записи и общих команд карточки атрибута пула.</summary>
public sealed partial class PhonePoolAttributeTests
{
    private static readonly PhonePoolAttributeKey Key = new(1002, "Тест+%");

    /// <summary>Query decoding проходит один раз. Разделитель US позволяет передать тип с двоеточием.</summary>
    [Theory]
    [InlineData("1002%3A%25%2B", "%+")]
    [InlineData("1002%1F%25%2B", "%+")]
    [InlineData("1002%1FA%3AB", "A:B")]
    [InlineData("1002%3A%253A", "%3A")]
    public void FrameworkDecodesCompositeKeyOnce(string encoded, string type)
    {
        var decoded = QueryHelpers.ParseQuery("?id=" + encoded)["id"].ToString();
        var key = Assert.IsType<PhonePoolAttributeKey>(PhonePoolAttributeKey.Parse(decoded));
        Assert.Equal(type, key.Type);
        Assert.Equal(1002, key.PoolId);
        var roundTrip = QueryHelpers.ParseQuery("?id=" + Uri.EscapeDataString(key.RouteId))["id"].ToString();
        Assert.Equal(key, PhonePoolAttributeKey.Parse(roundTrip));
    }

    /// <summary>Пустой, неоднозначный и дважды закодированный ключ отклоняются.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1002")]
    [InlineData("1002:a:b")]
    [InlineData("1002%3Aa")]
    [InlineData("1002:")]
    [InlineData("0:a")]
    [InlineData("pool:a")]
    [InlineData("1002:😀")]
    public void InvalidKeyIsRejected(string? id) => Assert.Null(PhonePoolAttributeKey.Parse(id));

    /// <summary>Границы SQL varchar проверяются без молчаливой замены Unicode.</summary>
    [Fact]
    public void ValueValidationKeepsEmptyAndCyrillic()
    {
        Assert.Null(PhonePoolAttributeKey.ValidateValue(""));
        Assert.Null(PhonePoolAttributeKey.ValidateValue(new string('Ж', 150)));
        Assert.NotNull(PhonePoolAttributeKey.ValidateValue(new string('Ж', 151)));
        Assert.NotNull(PhonePoolAttributeKey.ValidateValue("😀"));
        Assert.NotNull(PhonePoolAttributeKey.ValidateValue(null));
    }

    /// <summary>Прямые команды учитывают исходную версию и отзыв права.</summary>
    [Theory]
    [InlineData("insert-conflict")]
    [InlineData("update-missing")]
    [InlineData("delete-missing")]
    [InlineData("revoked")]
    public async Task ServerRejectsStaleOrUnauthorizedCommand(string condition)
    {
        using var connection = new Probe();
        var original = Value("исходное");
        if (condition == "insert-conflict") connection.Row = original;
        if (condition == "revoked") { connection.Allowed = false; connection.Row = original; }
        using var db = Db(connection);
        if (condition == "delete-missing")
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.DeleteAsync(db, "fixture", 4, Key, original, default));
        else
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, condition == "insert-conflict" ? null : original, "", default));
        Assert.Empty(connection.Writes);
        Assert.Equal(0, connection.Commits);
    }

    /// <summary>Потеря ответа COMMIT остаётся неопределённым исходом.</summary>
    [Fact]
    public async Task CommitResponseLossIsNotKnownRejection()
    {
        using var connection = new Probe { LoseCommit = true };
        using var db = Db(connection);
        var attemptId = Guid.NewGuid();
        await Assert.ThrowsAsync<PhonePoolAttributeUnknownException>(() =>
            PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, null, "", default, attemptId));
        Assert.Equal(["INSERT"], connection.Writes);
        Assert.Equal(2, connection.Commits);
        Assert.NotNull(connection.Row);
        Assert.Equal(InventoryOperationState.Confirmed, connection.Journal[attemptId].State);

        var verified = await PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, null, "", default, attemptId);
        Assert.Equal(connection.Row, verified.Attribute);
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            PhonePoolAttributeData.DeleteAsync(db, "fixture", 4, Key, verified.Attribute!, default, attemptId));
        Assert.Equal(["INSERT"], connection.Writes);
    }

    /// <summary>Сохранение и удаление одной канонической пары используют общий открытый слот.</summary>
    [Fact]
    public async Task UnresolvedWriteBlocksDeleteAndAnotherCircuitWithEquivalentType()
    {
        var original = Value("исходное");
        using var connection = new Probe { Row = original, TimeoutWrite = true };
        using var db = Db(connection);
        await Assert.ThrowsAsync<PhonePoolAttributeUnknownException>(() =>
            PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, original, "новое", default));

        connection.TimeoutWrite = false;
        var alternate = Key with { Type = Key.Type.ToUpperInvariant() + " " };
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            PhonePoolAttributeData.DeleteAsync(db, "fixture", 4, alternate, original, default));

        await using var context = CreateContext(connection, id: alternate.RouteId);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Есть незавершённая попытка изменения атрибута", page.Markup);
            Assert.True(Button(page, "Удалить").HasAttribute("disabled"));
        });
        Assert.Equal(["UPDATE"], connection.Writes);
        Assert.Equal(original, connection.Row);
    }

    /// <summary>Доверенные права проверяются повторно под транзакцией бизнес-записи.</summary>
    [Fact]
    public async Task RevokedRoleInsideTransactionSettlesWithoutWritingAttribute()
    {
        var original = Value("исходное");
        using var connection = new Probe { Row = original, RevokeRoleInsideTransaction = true };
        using var db = Db(connection);

        await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
            PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, original, "новое", default));

        Assert.Empty(connection.Writes);
        Assert.Equal(original, connection.Row);
        Assert.Equal(InventoryOperationState.Rejected, Assert.Single(connection.Journal.Values).State);
    }

    /// <summary>Применить создаёт пустое значение. Повторное применение и OK без правок не пишут.</summary>
    [Fact]
    public async Task ApplyCanInsertEmptyValueWithoutASecondInsert()
    {
        using var sql = new Probe();
        await using var context = CreateContext(sql);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Тестовый пул", page.Markup);
            Assert.False(Button(page, "Применить").HasAttribute("disabled"));
        });

        await Button(page, "Применить").ClickAsync(new MouseEventArgs());

        Assert.Equal(["INSERT"], sql.Writes);
        Assert.Equal("", sql.Row!.Value);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        await Button(page, "OK").ClickAsync(new MouseEventArgs());
        Assert.Single(sql.Writes);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.refreshOpener"]);
    }

    /// <summary>Отрицательный ответ при удалении оставляет значение и не обновляет родителя.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRunsOnlyAfterAffirmativeAnswer(bool accept)
    {
        using var sql = new Probe { Row = Value("сохранено") };
        await using var context = CreateContext(sql);
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.False(Button(page, "Удалить").HasAttribute("disabled")));
        var action = Button(page, "Удалить").ClickAsync(new MouseEventArgs());
        var text = accept ? "Да" : "Нет";
        provider.WaitForAssertion(() => Assert.Contains(provider.FindAll("button"), b => b.TextContent.Trim() == text));
        await provider.FindAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new MouseEventArgs());
        await action;
        Assert.Equal(accept ? new[] { "DELETE" } : Array.Empty<string>(), sql.Writes);
        Assert.Equal(!accept, sql.Row is not null);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.refreshOpener"]);
    }

    /// <summary>Отказ чтения типа оставляет карточку без команды создания значения.</summary>
    [Fact]
    public async Task UnknownTypeCannotBecomeNewValue()
    {
        using var sql = new Probe { TypeExists = false };
        await using var context = CreateContext(sql);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Тип атрибута не найден", page.Markup));
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(sql.Writes);
    }

    /// <summary>Редактирование существующей пары берёт последний введённый символ и обновляет аудит.</summary>
    [Fact]
    public async Task ExistingApplyUsesTheLatestValue()
    {
        using var sql = new Probe { Row = Value("исходное") };
        await using var context = CreateContext(sql);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("исходное", page.Markup));
        await page.FindAll("input").Last().InputAsync(new ChangeEventArgs { Value = "новое!" });
        await Button(page, "Применить").ClickAsync(new MouseEventArgs());

        Assert.Equal(["UPDATE"], sql.Writes);
        Assert.Equal("новое!", sql.Row!.Value);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
    }

    /// <summary>Нулевой результат UPDATE или DELETE оставляет прежнюю пару.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroAffectedRowsCannotCommit(bool delete)
    {
        var original = Value("исходное");
        using var connection = new Probe { Row = original, Affected = 0 };
        using var db = Db(connection);
        if (delete)
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.DeleteAsync(db, "fixture", 4, Key, original, default));
        else
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, original, "другое", default));
        Assert.Equal(1, connection.Commits);
        Assert.Equal(InventoryOperationState.Rejected, Assert.Single(connection.Journal.Values).State);
        Assert.Equal(original, connection.Row);
    }

    /// <summary>Недоступная карточка не запрашивает RAM-регистрацию и не показывает старый ввод.</summary>
    [Theory]
    [InlineData("pool")]
    [InlineData("type")]
    [InlineData("denied")]
    public async Task InaccessibleParentNeverAttachesOrShowsPriorInput(string failure)
    {
        using var sql = new Probe { PoolExists = failure != "pool", TypeExists = failure != "type", DenyRead = failure == "denied" };
        const string localValue = "Моё сохранённое значение";
        var snapshot = RecoverySnapshot(localValue);
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 7, null, null,
            new Dictionary<string, string> { ["main"] = snapshot }), hasPendingRaw: true);
        var page = context.Render<PhonePoolAttributeForm>();

        var failureText = failure switch
        {
            "pool" => "Пул телефона не найден",
            "type" => "Тип атрибута не найден",
            _ => "Нет доступа к данным пары атрибута",
        };
        page.WaitForAssertion(() => Assert.Contains(failureText, page.Markup));
        Assert.DoesNotContain(localValue, page.Markup);
        Assert.DoesNotContain("Тестовый пул", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.True(Button(page, "OK").HasAttribute("disabled"));
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Trim() == "Удалить");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.Empty(sql.Writes);
    }

    /// <summary>Сверка Unknown использует неизменный ключ из URL и сохраняет маркер.</summary>
    [Fact]
    public async Task LiveUnresolvedOperationStaysBlockedWhenParentsReturn()
    {
        using var sql = new Probe { Row = Value("Исходное значение") };
        var snapshot = RecoverySnapshot("Значение из попытки");
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "ppa-attempt", kind = "phone-pool-attribute.update", status = "unknown", revision = 8,
            data = snapshot, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 8, null, operation,
            new Dictionary<string, string> { ["main"] = snapshot }));
        var page = context.Render<PhonePoolAttributeForm>();

        page.WaitForAssertion(() => Assert.Contains("Обнаружена незавершённая операция", page.Markup));
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        sql.TypeExists = false;
        await Button(page, "Обновить").ClickAsync(new MouseEventArgs());
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        sql.TypeExists = true;
        sql.Row = Value("Значение из попытки");
        await Button(page, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("Данные в базе совпадают с отправленными", page.Markup);
        Assert.Contains("ppa-attempt", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(sql.Writes);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Сверка принимает только серверное подтверждение точной попытки, сотрудника, пары и команды.</summary>
    [Theory]
    [InlineData("save", "exact", true)]
    [InlineData("delete", "exact", true)]
    [InlineData("save", "other-attempt", false)]
    [InlineData("save", "other-kind", false)]
    [InlineData("save", "other-pair", false)]
    [InlineData("save", "other-employee", false)]
    [InlineData("save", "other-result", false)]
    public async Task RefreshOnlyCleansRevisionWithMatchingServerReceipt(string action, string proof, bool accepts)
    {
        const long revision = 23;
        var attemptId = Guid.NewGuid();
        var persistedId = proof == "other-attempt" ? Guid.NewGuid() : attemptId;
        var deleting = action == "delete";
        var command = proof == "other-kind" ? "delete" : action;
        const string value = "Значение из попытки";
        using var sql = new Probe { Row = deleting ? null : Value(value) };
        sql.Journal[persistedId] = new JournalRow(persistedId, proof == "other-employee" ? 19 : 4,
            proof == "other-pair" ? "phone-pool-attribute/369/Другой" : $"phone-pool-attribute/{Key.PoolId}/{Key.Type}",
            $"phone-pool-attribute.{command}", InventoryOperationState.Confirmed, persistedId,
            proof == "other-result" ? 369 : Key.PoolId);

        var snapshot = RecoverySnapshot(value);
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = attemptId.ToString("N"),
            kind = deleting ? "phone-pool-attribute.delete" : "phone-pool-attribute.update",
            status = "unknown", revision, data = snapshot,
            scopes = new[] { new { name = "main", typedRevision = revision, rawRevisions = new Dictionary<string, long>() } },
            acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, revision, null, operation,
            new Dictionary<string, string> { ["main"] = snapshot }));
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Обнаружена незавершённая операция", page.Markup));

        await Button(page, "Обновить").ClickAsync(new MouseEventArgs());

        var commits = context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        var receipts = context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"];
        if (accepts)
        {
            Assert.Equal(revision, Assert.Single(receipts).Arguments[1]);
            Assert.Equal(revision, Assert.Single(commits).Arguments[1]);
        }
        else
        {
            Assert.Empty(receipts);
            Assert.Empty(commits);
            Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        }
        Assert.Empty(sql.Writes);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
    }

    /// <summary>Восстановление доступа не заменяет исходный снимок актуальной версией без сверки.</summary>
    [Fact]
    public async Task ReturnOfAccessWithChangedRowPreservesLocalDraftAndBlocksWrite()
    {
        using var sql = new Probe { Row = Value("Новое значение в БД") };
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 7, null, null,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot("Моё сохранённое значение") }));
        var page = context.Render<PhonePoolAttributeForm>();

        page.WaitForAssertion(() => Assert.Contains("Карточка изменилась", page.Markup));
        Assert.Contains("Моё сохранённое значение", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(sql.Writes);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    /// <summary>Обновление получает текущую карточку только в пределах действующих прав чтения.</summary>
    [Fact]
    public async Task RefreshLoadsReadOnlyPairWithoutAttachingPriorDraft()
    {
        using var sql = new Probe { TypeExists = false, EditAllowed = false };
        const string priorValue = "Моё сохранённое значение";
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 7, null, null,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot(priorValue) }));
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Тип атрибута не найден", page.Markup));
        Assert.DoesNotContain(priorValue, page.Markup);

        sql.TypeExists = true;
        sql.Row = Value("Актуальное значение");
        await Button(page, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("Актуальное значение", page.Markup);
        Assert.DoesNotContain(priorValue, page.Markup);
        Assert.True(page.Find("input[data-draft-key='pool-attribute.value']").HasAttribute("readonly"));
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.True(Button(page, "Удалить").HasAttribute("disabled"));

        sql.DenyRead = true;
        await Button(page, "Обновить").ClickAsync(new MouseEventArgs());
        Assert.DoesNotContain("Актуальное значение", page.Markup);
        Assert.Contains("Нет доступа к данным пары атрибута", page.Markup);
        Assert.DoesNotContain(priorValue, page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(sql.Writes);
    }

    /// <summary>Ошибочный маршрут не показывает ввод, который относился к другому ключу.</summary>
    [Fact]
    public async Task ReadOnlyRouteChangeNeverAttachesPreviousCompositeKey()
    {
        using var sql = new Probe { TypeExists = false };
        const string priorValue = "Значение предыдущего маршрута";
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 7, null, null,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot(priorValue) }));
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Тип атрибута не найден", page.Markup));

        await page.InvokeAsync(() => context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/Equipment/PhonePoolAtribute_form?id=invalid"));

        page.WaitForAssertion(() => Assert.Contains("Не удалось установить идентификатор", page.Markup));
        Assert.DoesNotContain(priorValue, page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Empty(sql.Writes);
    }


    /// <summary>Доступ к черновику требует валидной пары и доверенной идентичности.</summary>
    [Theory]
    [InlineData("invalid", true)]
    [InlineData(null, false)]
    public async Task InvalidKeyOrIdentityDoesNotRegisterDraft(string? id, bool authenticated)
    {
        using var sql = new Probe();
        await using var context = CreateContext(sql, id: id, authenticated: authenticated);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains(authenticated ? "Не удалось установить идентификатор" : "Не удалось подтвердить текущего сотрудника", page.Markup));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(sql.Writes);
    }

    /// <summary>Восстановление подтверждает точную пару и не раскрывает значение атрибута.</summary>
    [Theory]
    [InlineData("owned")]
    [InlineData("owned-denied")]
    [InlineData("role-revoked")]
    [InlineData("wrong-key")]
    [InlineData("wrong-actor")]
    [InlineData("legacy-insert")]
    public async Task SubmittedAttributeView_ProtectsOpaqueValues(string mode)
    {
        using var sql = new Probe { Row = Value("original-value") };
        const string privateValue = "fixture-secret-unclassified-value";
        var exactKey = mode == "wrong-key" ? new PhonePoolAttributeKey(Key.PoolId, "Другой тип").RouteId : Key.RouteId;
        var data = JsonSerializer.Serialize(new
        {
            Original = new PhonePoolAttributeForm.FormState("original-value"),
            Current = new PhonePoolAttributeForm.FormState(privateValue),
            Version = mode == "legacy-insert" ? null : new { Key = exactKey, ChangedBy = 4, ChangedAt = DateTime.UnixEpoch },
        });
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "ppa-submitted", kind = mode == "legacy-insert" ? "phone-pool-attribute.insert" : "phone-pool-attribute.update",
            status = "unknown", revision = 21, data, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 21, null, operation,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot("local-draft") }));
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Посмотреть снимок попытки", page.Markup));
        if (mode == "wrong-actor") sql.EmployeeId = 8;
        if (mode == "owned-denied") sql.DenyRead = true;
        if (mode == "role-revoked") sql.Allowed = false;

        await page.InvokeAsync(() => Button(page, "Посмотреть снимок попытки").ClickAsync());

        if (mode == "owned")
            Assert.Contains("Значение атрибута скрыто", page.Find("[data-testid=submitted-attempt]").TextContent);
        else
        {
            Assert.Empty(page.FindAll("[data-testid=submitted-attempt]"));
            Assert.Contains("Не удалось подтвердить доступ к снимку", page.Markup);
        }
        Assert.DoesNotContain(privateValue, page.Markup);
        Assert.Contains("local-draft", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(sql.Writes);
    }

    private static string RecoverySnapshot(string value) => JsonSerializer.Serialize(new
    {
        Original = new PhonePoolAttributeForm.FormState("Исходное значение"),
        Current = new PhonePoolAttributeForm.FormState(value),
    });

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<PhonePoolAttributeForm> page, string name) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == name);

    private static DbManager Db(Probe connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
    }

    private static PhonePoolAttributeValue Value(string value) => new(value, 4, new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc), "Инженер");

    /// <summary>Прежняя собственная попытка блокирует запись карточки до выдачи подтверждённого исхода.</summary>
    [Theory]
    [InlineData("phone-pool-attribute.insert", "phone-pool-attribute.save")]
    [InlineData("phone-pool-attribute.update", "phone-pool-attribute.save")]
    [InlineData("phone-pool-attribute.delete", "phone-pool-attribute.delete")]
    public async Task LegacyOperationImportsOnlyOwnCanonicalPairBeforeRam(string clientKind, string serverKind)
    {
        using var sql = new Probe { Row = Value("В базе") };
        var attempt = Guid.NewGuid();
        await using var context = CreateContext(sql);
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", call =>
            Equals(call.Arguments[0], "phone-pool-attribute") && Equals(call.Arguments[1], Key.RouteId) &&
            Equals(call.Arguments[2], "4"))
            .SetResult([new(attempt.ToString("N"), clientKind, Key.RouteId)]);
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam",
            _ => sql.LegacyJournal.ContainsKey(attempt) &&
                 context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"].Count == 1)
            .SetResult(new ClayDraftRestore("legacy-ppa", false, 0, null, null));
        var page = context.Render<PhonePoolAttributeForm>();

        page.WaitForAssertion(() => Assert.Contains("Повтор записи заблокирован", page.Markup));
        var row = Assert.Single(sql.LegacyJournal).Value;
        Assert.Equal(InventoryOperationState.Unknown, row.State);
        Assert.Equal(serverKind, row.CommandKind);
        Assert.Equal($"phone-pool-attribute/{Key.PoolId}/{Key.Type}", row.ConflictKey);
        var read = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]);
        Assert.Equal(read.Arguments[3],
            Assert.Single(context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]).Arguments[0]);
        Assert.Empty(sql.Journal);
        Assert.Empty(sql.Writes);
    }

    /// <summary>Чужая или неканоническая пара не получает свидетельство в журнале.</summary>
    [Fact]
    public async Task LegacyOperationWithWrongPairNeverRegistersOrWrites()
    {
        using var sql = new Probe { Row = Value("В базе") };
        await using var context = CreateContext(sql);
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([new(Guid.NewGuid().ToString("N"), "phone-pool-attribute.update", "2:другая пара")]);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.True(Button(page, "Применить").HasAttribute("disabled")));
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.finishLegacyOperationEvidenceTransfer"]));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(sql.LegacyJournal);
        Assert.Empty(sql.Writes);
    }


    private static BunitContext CreateContext(Probe connection, ClayDraftRestore? restore = null, string? id = null,
        bool hasPendingRaw = false, bool authenticated = true)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([]);
        context.JSInterop.SetupVoid("clayDraftStore.closeRam", call => call.Arguments.Count == 1).SetVoidResult();
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", call => call.Arguments.Count == 2).SetResult(true);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(1, false, false));
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(restore ?? new ClayDraftRestore("phone-pool-fixture", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], hasPendingRaw ? ["data:pool-attribute.value"] : []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(!hasPendingRaw, hasPendingRaw ? ["main"] : [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 0, 0));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"fixture","kind":"phone-pool-attribute.insert","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddScoped(_ => Db(connection));
        context.Services.AddScoped(_ => new ClayDraftService(new JournalRuntime(context.JSInterop.JSRuntime)));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<AuthenticationStateProvider>(new FixtureAuthentication(authenticated));
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Render<MudPopoverProvider>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/Equipment/PhonePoolAtribute_form?id=" + Uri.EscapeDataString(id ?? Key.RouteId));
        return context;
    }

    private sealed class FixtureAuthentication(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(authenticated ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test") : new ClaimsIdentity())));
    }

    private sealed class JournalRuntime(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            var result = await inner.InvokeAsync<T>(identifier, token, args);
            if (identifier != "clayDraftStore.beginOperationIntent" || result is not string reply) return result;
            using var intent = JsonDocument.Parse((string)args![1]!);
            var payload = intent.RootElement;
            var operation = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            operation["attemptId"] = payload.GetProperty("attemptId").GetString();
            operation["kind"] = payload.GetProperty("kind").GetString();
            operation["data"] = payload.GetProperty("data").GetString();
            operation["scopes"] = System.Text.Json.Nodes.JsonNode.Parse(
                """[{"name":"main","typedRevision":1,"rawRevisions":{}}]""");
            return (T)(object)operation.ToJsonString();
        }
    }

    private sealed record JournalRow(Guid AttemptId, int EmployeeId, string ConflictKey, string CommandKind,
        InventoryOperationState State, Guid OpenSlot, int? ResultEntityId = null);

    private sealed class Probe : DbConnection
    {
        public bool Allowed { get; set; } = true;
        public bool EditAllowed { get; set; } = true;
        public int EmployeeId { get; set; } = 4;
        public bool PoolExists { get; init; } = true;
        public bool TypeExists { get; set; } = true;
        public bool DenyRead { get; set; }
        public bool LoseCommit { get; init; }
        public bool TimeoutWrite { get; set; }
        public bool RevokeRoleInsideTransaction { get; init; }
        public int Affected { get; init; } = 1;
        public int Commits { get; set; }
        public Func<Task>? BeforeCardRead { get; set; }
        public PhonePoolAttributeValue? Row { get; set; }
        public List<string> Writes { get; } = [];
        public Dictionary<Guid, JournalRow> Journal { get; } = [];
        public Dictionary<Guid, JournalRow> LegacyJournal { get; } = [];
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            Assert.Equal(IsolationLevel.Serializable, level);
            return new ProbeTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new Command(this);
    }

    private sealed class ProbeTransaction(Probe owner) : DbTransaction
    {
        internal PhonePoolAttributeValue? Row { get; set; } = owner.Row;
        internal JournalRow? JournalChange { get; set; }
        internal Dictionary<Guid, JournalRow> LegacyChanges { get; } = [];
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => owner;
        public override void Commit()
        {
            owner.Row = Row;
            if (JournalChange is { } journal) owner.Journal[journal.AttemptId] = journal;
            foreach (var entry in LegacyChanges) owner.LegacyJournal[entry.Key] = entry.Value;
            owner.Commits++;
            if (owner.LoseCommit && JournalChange is { State: InventoryOperationState.Confirmed })
                throw new TimeoutException();
        }
        public override void Rollback() { }
    }

    private sealed class Command(Probe owner) : DbCommand
    {
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        public override void Prepare() { }
        public override void Cancel() { }
        public override object ExecuteScalar()
        {
            Assert.Contains("UPDATE dbo.ClayzorOperationJournal", CommandText);
            var id = (Guid)Parameter("AttemptId")!;
            if (!owner.Journal.TryGetValue(id, out var current) ||
                current.State != (InventoryOperationState)(byte)Parameter("ExpectedState")! ||
                !Equals(current.EmployeeId, Parameter("EmployeeId")) || !Equals(current.OpenSlot, Parameter("OpenSlot")) ||
                !Equals(current.CommandKind, Parameter("CommandKind")) || !Equals(current.ConflictKey, Parameter("ConflictKey")))
                return 0;
            var next = current with
            {
                OpenSlot = (bool)Parameter("Settled")! ? id : current.OpenSlot,
                State = (InventoryOperationState)(byte)Parameter("NextState")!,
                ResultEntityId = Parameter("ResultEntityId") as int?,
            };
            if (DbTransaction is ProbeTransaction transaction) transaction.JournalChange = next;
            else
            {
                Assert.Equal(InventoryOperationState.Rejected, next.State);
                owner.Journal[id] = next;
            }
            return 1;
        }

        public override int ExecuteNonQuery()
        {
            Assert.Contains("INSERT INTO dbo.ClayzorOperationJournal", CommandText);
            var transaction = Assert.IsType<ProbeTransaction>(DbTransaction);
            Assert.Equal(5, Parameters.Count);
            transaction.JournalChange = new JournalRow((Guid)Parameter("AttemptId")!, (int)Parameter("EmployeeId")!,
                (string)Parameter("ConflictKey")!, (string)Parameter("CommandKind")!, InventoryOperationState.Reserved,
                (Guid)Parameter("OpenSlot")!);
            return 1;
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct)
        {
            if (DbTransaction is null && owner.BeforeCardRead is { } wait &&
                CommandText.Contains("@poolId", StringComparison.Ordinal))
                await wait();
            return ExecuteDbDataReader(behavior);
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("INSERT INTO dbo.ClayzorLegacyOperationEvidence", StringComparison.Ordinal))
            {
                var transaction = Assert.IsType<ProbeTransaction>(DbTransaction);
                var id = (Guid)Parameter("AttemptId")!;
                var evidence = new JournalRow(id, (int)Parameter("EmployeeId")!, (string)Parameter("ConflictKey")!,
                    (string)Parameter("CommandKind")!, InventoryOperationState.Unknown, Guid.Empty);
                transaction.LegacyChanges.TryAdd(id, owner.LegacyJournal.GetValueOrDefault(id) ?? evidence);
                return Rows([("Value", typeof(bool))], [transaction.LegacyChanges[id] == evidence]).CreateDataReader();
            }
            if (CommandText.Contains("dbo.ClayzorOperationJournal", StringComparison.Ordinal))
                return ReadJournal();
            if (CommandText.Contains("SUSER_SID()")) return Rows([("КодСотрудника", typeof(int))], [owner.EmployeeId]).CreateDataReader();
            if (CommandText.Contains("fn_ТекущиеРоли"))
                return Rows([("RoleId", typeof(int)), ("PersonId", typeof(int))],
                    owner.Allowed && !(owner.RevokeRoleInsideTransaction && DbTransaction is not null)
                        ? [(int)Роли.Дежурный_инженер, 0] : null).CreateDataReader();
            if (CommandText.Contains("HAS_PERMS_BY_NAME"))
                return Rows([("CanRead", typeof(bool)), ("CanInsert", typeof(bool)), ("CanUpdate", typeof(bool)),
                    ("CanDelete", typeof(bool))], [true, owner.EditAllowed, owner.EditAllowed, owner.EditAllowed]).CreateDataReader();

            Assert.Equal(Key.PoolId, Parameter("poolId"));
            Assert.Equal(Key.Type, ((string)Parameter("type")!).TrimEnd(' '), StringComparer.OrdinalIgnoreCase);
            if (CommandText.Contains("@@ROWCOUNT"))
            {
                var transaction = Assert.IsType<ProbeTransaction>(DbTransaction);
                var operation = CommandText[..CommandText.IndexOf(' ')];
                owner.Writes.Add(operation);
                if (owner.TimeoutWrite) throw new TimeoutException();
                if (owner.Affected == 1) transaction.Row = operation == "DELETE" ? null : Value((string)Parameter("value")!);
                return Rows([("Affected", typeof(int))], [owner.Affected]).CreateDataReader();
            }

            if (owner.DenyRead && DbTransaction is null) throw SqlExceptionFactory.Create(229, "Тестовый отказ SELECT");
            var row = DbTransaction is ProbeTransaction tx ? tx.Row : owner.Row;
            if (DbTransaction is not null) Assert.Contains("UPDLOCK", CommandText);
            return new DataTableReader([
                Rows([("ПулТелефона", typeof(string))], owner.PoolExists ? ["Тестовый пул"] : null),
                Rows([("Type", typeof(string)), ("Description", typeof(string))], owner.TypeExists ? [Key.Type, "Описание"] : null),
                Rows([("Value", typeof(string)), ("ChangedBy", typeof(int)), ("ChangedAt", typeof(DateTime)), ("ChangedByName", typeof(string))],
                    row is null ? null : [row.Value, row.ChangedBy, row.ChangedAt, row.ChangedByName]),
            ]);
        }

        private DbDataReader ReadJournal()
        {
            if (CommandText.Contains("WHERE AttemptId = @AttemptId;", StringComparison.Ordinal))
            {
                owner.Journal.TryGetValue((Guid)Parameter("AttemptId")!, out var row);
                return Rows([("AttemptId", typeof(Guid)), ("EmployeeId", typeof(int)), ("ConflictKey", typeof(string)),
                    ("CommandKind", typeof(string)), ("State", typeof(byte)), ("ResultEntityId", typeof(int))],
                    row is null ? null : [row.AttemptId, row.EmployeeId, row.ConflictKey, row.CommandKind, (byte)row.State,
                        row.ResultEntityId]).CreateDataReader();
            }
            if (CommandText.Contains("WHERE ConflictKey = @ConflictKey AND OpenSlot = @OpenSlot", StringComparison.Ordinal))
                return Rows([("Value", typeof(bool))], [owner.Journal.Values.Concat(owner.LegacyJournal.Values).Any(row =>
                    Equals(row.ConflictKey, Parameter("ConflictKey")) && Equals(row.OpenSlot, Parameter("OpenSlot")))])
                    .CreateDataReader();

            Assert.Contains("SELECT ConflictKey", CommandText);
            var table = Rows([("ConflictKey", typeof(string))], null);
            foreach (var row in owner.Journal.Values.Concat(owner.LegacyJournal.Values).Where(row =>
                Equals(row.CommandKind, Parameter("CommandKind")) && Equals(row.OpenSlot, Parameter("OpenSlot"))))
                table.Rows.Add(row.ConflictKey);
            return table.CreateDataReader();
        }

        private object? Parameter(string name) =>
            Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == name).Value;

        private static DataTable Rows((string Name, Type Type)[] columns, object?[]? values)
        {
            var table = new DataTable();
            foreach (var (name, type) in columns) table.Columns.Add(name, type);
            if (values is not null) table.Rows.Add(values.Select(v => v ?? DBNull.Value).ToArray());
            return table;
        }
    }
}

