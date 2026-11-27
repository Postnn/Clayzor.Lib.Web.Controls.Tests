using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет перенос через реальный адаптер формы, SQL-фикстуру и браузерную границу.</summary>
public sealed class TerritorialEntityMoveCleanupTests
{
    [Fact]
    public async Task FreshDocumentShowsOwnConfirmedMoveWithoutStartingAnotherOperation()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var id = Guid.NewGuid();
        database.Journal.Add(id, new(id, 4, "territory/tree", "territory.move", InventoryOperationState.Confirmed, 8,
            ["territory/move/8/5/2/3/9/root/11/14"]));

        var form = RenderSearch(context, database);

        Assert.Equal("Последний перенос территории №8 под №9 подтверждён.",
            form.Find("[data-testid=latest-move-outcome]").TextContent.Trim());
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, database.Updates);
        Assert.Equal(0, database.Commits);
    }

    [Fact]
    public async Task OwnMoveReceiptDoesNotRevealForeignPendingOrTreatCardWriteAsMove()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        Add(4, "territory.move", InventoryOperationState.Confirmed, 8, "territory/move/8/5/2/3/root");
        Add(4, "territory.update", InventoryOperationState.Confirmed, 726, "territory/726");
        Add(32, "territory.move", InventoryOperationState.Unknown, null, "territory/move/887/5/2/3/root");

        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.Equal("Последний перенос территории №8 в корень подтверждён.",
            form.Find("[data-testid=latest-move-outcome]").TextContent.Trim());
        Assert.Contains("Перенос пока недоступен", form.Markup);
        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, database.Updates);

        void Add(int employee, string command, InventoryOperationState state, int? result, string scope)
        {
            var id = Guid.NewGuid();
            database.Journal.Add(id, new(id, employee, "territory/tree", command, state, result, [scope]));
        }
    }

    [Theory]
    [InlineData("territory/move/08/5/2/3/root", 8, false)]
    [InlineData("territory/move/8/5/3/2/root", 8, false)]
    [InlineData("territory/move/8/5/2/3/root", 9, false)]
    [InlineData("territory/move/8/5/2/3/root", 8, true)]
    public async Task ReceiptWithUnprovenSourceOrResultIsNotPublished(string scope, int result, bool duplicateScope)
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var id = Guid.NewGuid();
        database.Journal.Add(id, new(id, 4, "territory/tree", "territory.move", InventoryOperationState.Confirmed, result,
            duplicateScope ? [scope, "territory/move/8/5/2/3/9/root/11/14"] : [scope]));

        var form = RenderSearch(context, database);

        Assert.Empty(form.FindAll("[data-testid=latest-move-outcome]"));
        Assert.Contains("Не удалось прочитать результат переноса", form.Markup);
        Assert.Equal(0, database.Updates);
    }

    [Fact]
    public async Task ReadOutcomeChecksFreshSqlRightBeforeReadingJournal()
    {
        var database = new MoveConnection();
        DapperColumnMapper.Initialize();
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => database);
        database.QueueMoveRights(canUpdate: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            TerritorialEntityData.ReadLatestMoveOutcomeAsync(db, "fixture", 4, default));

        Assert.Equal(0, database.LatestMoveReads);
    }

    [Fact]
    public async Task AutomaticCleanupRetriesWithoutRepeatingConfirmedMove()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var parent = tree.RootNodes.Single(node => node.Id == "5");
        var destination = tree.RootNodes.Single(node => node.Id == "9");
        var child = ChildOf(parent);
        await form.InvokeAsync(() => tree.HandleNodeClick(parent));
        var url = form.Find("iframe").GetAttribute("src")!;
        database.QueuePane("Старый родитель");

        var module = context.JSInterop.SetupModule("./js/territorialEntitySearch.js");
        var failCleanup = true;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!failCleanup) return false;
            failCleanup = false;
            return true;
        })
            .SetException(new JSException("Очистка недоступна"));
        module.Setup<bool>("refreshMetadataFrame", _ => true).SetResult(true);

        var moved = await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination));

        Assert.True(moved);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.DoesNotContain("Перенос не подтверждён", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Equal("Территории в Старый родитель", form.Find("iframe").GetAttribute("title"));
        Assert.Equal(url, form.Find("iframe").GetAttribute("src"));
        Assert.Equal(9, database.ParentId);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.commitOperation"].Count);
        Assert.All(context.JSInterop.Invocations["clayDraftStore.commitOperation"], call => Assert.Equal(11L, call.Arguments[1]));
        Assert.Empty(database.Reads);
    }

    [Fact]
    public async Task LostCommitAcknowledgementUsesAuthoritativeReceiptAndPreservesSingleUpdate()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection { FailCommitAcknowledgement = true };
        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.True(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));

        Assert.DoesNotContain("Результат переноса не подтверждён", form.Markup);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
        Assert.Equal(9, database.ParentId);
        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.Equal(InventoryOperationState.Confirmed, Assert.Single(database.Journal).Value.State);
        Assert.Empty(database.Reads);
    }


    [Fact]
    public async Task SuspendedPreparationLeavesTheDomainUntouched()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var form = RenderSearch(context, database, failBegin: true);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Contains("Запись не начата", form.Markup);
        Assert.DoesNotContain("Сверить родителя", form.Markup);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Equal(0, database.Updates);
        Assert.Equal(0, database.Commits);
        Assert.Equal(4, database.Reads.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ReopenedJournalOnlyFinishesAnExactlyMatchedKnownOutcome(bool matching, bool rejected)
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var id = Guid.NewGuid();
        const long revision = 11;
        var intent = JsonSerializer.Serialize(new
        {
            From = new { Id = 8, ParentId = 5, Left = 2, Right = 3 },
            To = new { Id = 9, ParentId = (int?)null, Left = 11, Right = 14 },
            AttemptId = id,
        });
        var storedOperation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = id.ToString("N"), kind = "territory.move", status = "confirmed", revision,
            data = intent, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        var result = JsonSerializer.Serialize(new
        {
            AttemptId = id.ToString("N"),
            Revision = revision,
            Rejection = rejected ? "Конкурентный перенос уже изменил строку." : null,
        });
        if (matching)
            database.Journal[id] = new MoveJournalRow(id, 4, "territory/tree", "territory.move",
                rejected ? InventoryOperationState.Rejected : InventoryOperationState.Confirmed,
                rejected ? null : 8, ["territory/move/8/5/2/3/9/root/11/14"]);
        var form = RenderSearch(context, database,
            new ClayDraftRestore("move-fixture", true, revision, null, storedOperation,
                new Dictionary<string, string> { ["move-result"] = result }));
        if (matching)
        {
            var commit = Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
            Assert.Equal(revision, commit.Arguments[1]);
            if (rejected) Assert.Contains("Серверный журнал подтвердил отказ", form.Markup);
        }
        else
        {
            Assert.Contains("Результат переноса не подтверждён", form.Markup);
            Assert.DoesNotContain("Повторить очистку", form.Markup);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        }
        Assert.Equal(0, database.Updates);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Чужая открытая попытка блокирует всё вложенное дерево ещё до браузерного Begin.</summary>
    [Fact]
    public async Task OpenServerMoveBlocksAnotherEmployeeWithoutBrowserAttempt()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var id = Guid.NewGuid();
        database.Journal[id] = new MoveJournalRow(id, 77, "territory/tree", "territory.move",
            InventoryOperationState.Reserved, null, ["territory/move/91/root/19/21/root"]);

        var form = RenderSearch(context, database);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Contains("Перенос пока недоступен", form.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, database.Updates);
    }

    /// <summary>Тот же GUID с другим снимком назначения не получает подтверждение переноса.</summary>
    [Fact]
    public async Task DurableReceiptMatchesBothCapturedNodesAndDoesNotRepeatUpdate()
    {
        DapperColumnMapper.Initialize();
        using var database = new MoveConnection { FailCommitAcknowledgement = true };
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => database);
        var id = Guid.NewGuid();
        var source = new TerritorialEntityData.MoveNode(8, 5, 2, 3);
        var target = new TerritorialEntityData.MoveNode(9, null, 11, 14);
        database.QueueMoveRights();
        database.QueueMoveRights();
        var first = await TerritorialEntityData.ReparentAsync(db, "fixture", 4, id, source, target, default);
        database.QueueMoveRights();
        var replay = await TerritorialEntityData.ReparentAsync(db, "fixture", 4, id, source, target, default);
        Assert.Equal(first, replay);
        Assert.Equal(InventoryOperationState.Confirmed, replay.State);

        database.QueueMoveRights();
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            TerritorialEntityData.ReadMoveReceiptAsync(db, "fixture", 4, id, source, target with { Right = 16 }, default));

        Assert.Equal(1, database.Updates);
        Assert.Equal(1, database.Commits);
        Assert.Empty(database.Reads);
    }


    /// <summary>Отзыв права между резервом и UPDATE подтверждается без переноса.</summary>
    [Fact]
    public async Task MoveChecksSqlUpdateRightsAgainInTheWriteTransaction()
    {
        DapperColumnMapper.Initialize();
        using var database = new MoveConnection();
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => database);
        var id = Guid.NewGuid();
        var source = new TerritorialEntityData.MoveNode(8, 5, 2, 3);
        var target = new TerritorialEntityData.MoveNode(9, null, 11, 14);
        database.QueueMoveRights();
        database.QueueMoveRights(canUpdate: false);

        await Assert.ThrowsAsync<TerritorialEntityRuleException>(() =>
            TerritorialEntityData.ReparentAsync(db, "fixture", 4, id, source, target, default));

        Assert.Equal(InventoryOperationState.Rejected, database.Journal[id].State);
        Assert.Equal(5, database.ParentId);
        Assert.Equal(0, database.Updates);
    }


    private sealed class MoveAcknowledgement(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var value = await inner.InvokeAsync<T>(identifier, cancellationToken, args);
            if (identifier != "clayDraftStore.beginOperationIntent" || value is not string json) return value;
            using var requested = JsonDocument.Parse((string)args![1]!);
            var result = JsonNode.Parse(json)!;
            foreach (var name in new[] { "attemptId", "kind", "data" })
                result[name] = requested.RootElement.GetProperty(name).GetString();
            return (T)(object)result.ToJsonString();
        }
    }

    [Fact]
    public async Task LegacyMarkerWithoutStableGuidIsBlockedWithoutRevivingItsRawContents()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        context.JSInterop.Setup<string?>("sessionStorage.getItem", invocation => Equals(invocation.Arguments[0], "territory-move:4"))
            .SetResult("""{"From":{"Id":8,"ParentId":5,"Left":2,"Right":3},"To":{"Id":9,"ParentId":null,"Left":11,"Right":14}}""");
        var form = RenderSearch(context, database, hasUnidentifiedLegacyMarker: true);
        var tree = form.FindComponent<ClayTreeView>().Instance;
        var child = ChildOf(tree.RootNodes.Single(node => node.Id == "5"));
        var destination = tree.RootNodes.Single(node => node.Id == "9");

        Assert.False(await form.InvokeAsync(() => tree.Options.ReparentNodeAsync!(child, destination)));
        Assert.Contains("без подтверждённого идентификатора", form.Markup);
        Assert.DoesNotContain("Сверить родителя", form.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]);
        Assert.DoesNotContain("Повторить очистку", form.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, database.Updates);
    }

    /// <summary>Старый перенос удерживает глобальный слот до сверки без восстановления формы из IndexedDB.</summary>
    [Fact]
    public async Task LegacyTransferCompletesBeforeRamRegistrationAndBlocksMove()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var id = Guid.NewGuid();
        var transfer = new ClayLegacyDraftOperationEvidence(id.ToString("N"), "territory.move",
            """{"From":{"Id":8,"ParentId":5,"Left":2,"Right":3},"To":{"Id":9,"ParentId":null,"Left":11,"Right":14}}""");
        var form = RenderSearch(context, database, legacyEvidence: [transfer]);

        Assert.Equal(InventoryOperationState.Unknown, database.LegacyEvidence[id].State);
        Assert.Equal("territory/tree", database.LegacyEvidence[id].ConflictKey);
        Assert.Empty(database.Journal);
        var calls = await form.InvokeAsync(() => context.JSInterop.Invocations.ToList());
        var begin = Assert.Single(calls.Where(call => call.Identifier == "clayDraftStore.beginLegacyOperationEvidenceTransfer"));
        Assert.Equal(new object[] { "territory-tree-move", "unrestricted", "4" }, begin.Arguments.Take(3));
        var finish = Assert.Single(calls.Where(call => call.Identifier == "clayDraftStore.finishLegacyOperationEvidenceTransfer"));
        var register = Assert.Single(calls.Where(call => call.Identifier == "clayDraftStore.registerRam"));
        var retire = Assert.Single(calls.Where(call => call.Identifier == "clayDraftStore.retireLegacyOperationEvidenceTransfer"));
        Assert.True(calls.IndexOf(register) < calls.IndexOf(retire));
        Assert.True(calls.IndexOf(retire) < calls.IndexOf(finish));
        Assert.Equal(begin.Arguments[3], finish.Arguments[0]);
        Assert.Equal(begin.Arguments[3], retire.Arguments[0]);
        Assert.True(Guid.TryParse(Assert.IsType<string>(register.Arguments[5]), out _));
        Assert.Contains("Перенос пока недоступен", form.Markup);
        Assert.Equal("Сервер пока не подтвердил перенос территории №8 под №9.",
            form.Find("[data-testid=latest-move-outcome]").TextContent.Trim());
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.listRecoveryCandidates"]);
        Assert.Equal(0, database.Updates);
    }

    /// <summary>Технический Dispose лишь отсоединяет принадлежавший странице экземпляр.</summary>
    [Fact]
    public async Task MoveTechnicalDisposeNeverDeletesTheRamDraft()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        RenderSearch(context, database);

        await context.DisposeComponentsAsync();

        Assert.Single(context.JSInterop.Invocations["clayDraftStore.unregister"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Equal(0, database.Updates);
    }

    /// <summary>Переход ожидает удаления той же принятой ревизии и может повторяться без SQL.</summary>
    [Fact]
    public async Task ControlledMoveNavigationRetriesPhysicalCleanupOnly()
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var form = RenderSearch(context, database);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var original = navigation.Uri;
        var fail = true;
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ =>
        {
            if (!fail) return false;
            fail = false;
            return true;
        }).SetException(new JSException("fixture deletion failed"));

        await form.InvokeAsync(() => navigation.NavigateTo("/next"));
        Assert.Equal(original, navigation.Uri);
        Assert.Contains("Повторите переход", form.Markup);

        await form.InvokeAsync(() => navigation.NavigateTo("/next"));
        Assert.EndsWith("/next", navigation.Uri);
        var closes = context.JSInterop.Invocations["clayDraftStore.closeRam"];
        Assert.Equal(2, closes.Count);
        Assert.All(closes, call =>
        {
            Assert.Equal("move-fixture", call.Arguments[0]);
            Assert.Equal(19L, call.Arguments[1]);
        });
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.readEditorScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, database.Updates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NavigationOnlyDeletesLocalUnknownAfterServerEvidenceIsSecured(bool canKeepEvidence)
    {
        await using var context = new BunitContext();
        var database = new MoveConnection();
        var form = RenderSearch(context, database);
        database.Reads.Clear();
        database.QueueMoveRights(canKeepEvidence);
        var id = Guid.NewGuid();
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(23, false, true));
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence>("clayDraftStore.readCloseOperationEvidence", _ => true)
            .SetResult(new ClayLegacyDraftOperationEvidence(id.ToString("N"), "territory.move",
                """{"From":{"Id":8,"ParentId":5,"Left":2,"Right":3},"To":null}"""));
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var original = navigation.Uri;

        await form.InvokeAsync(() => navigation.NavigateTo("/next"));

        if (canKeepEvidence)
        {
            Assert.EndsWith("/next", navigation.Uri);
            var evidence = Assert.Single(database.LegacyEvidence).Value;
            Assert.Equal(id, evidence.AttemptId);
            Assert.Equal(InventoryOperationState.Unknown, evidence.State);
            Assert.Equal("territory/move/8/5/2/3/root", Assert.Single(evidence.Scopes));
            Assert.Equal(23L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.closeRam"]).Arguments[1]);
        }
        else
        {
            Assert.Equal(original, navigation.Uri);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
            Assert.Empty(database.LegacyEvidence);
        }
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, database.Updates);
    }


    private static ClayTreeNode ChildOf(ClayTreeNode parent) => new()
    {
        Id = "8", RawId = 8, Left = 2, Right = 3, Parent = parent,
    };

    private static IRenderedComponent<TerritorialEntitySearch> RenderSearch(BunitContext context, MoveConnection database,
        ClayDraftRestore? restore = null, bool failBegin = false, ClayLegacyDraftOperationEvidence[]? legacyEvidence = null,
        bool hasUnidentifiedLegacyMarker = false)
    {
        DapperColumnMapper.Initialize();
        database.QueueAuth();
        if (legacyEvidence is { Length: > 0 }) database.QueueMoveRights();
        if (restore?.OperationSnapshot is not null) database.QueueMoveRights();
        else if (!hasUnidentifiedLegacyMarker) database.QueueMoveRights();
        database.QueueRoots();
        if (restore is null)
        {
            database.Reads.Enqueue(Script.Rows(Script.Row(("Id", 4))));
            database.QueueMoveRights();
            database.QueueMoveRights();
        }
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.registerRam", _ => true)
            .SetResult(restore ?? new ClayDraftRestore("move-fixture", false, 0, null, null));
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult(legacyEvidence ?? []);
        context.JSInterop.SetupVoid("clayDraftStore.finishLegacyOperationEvidenceTransfer", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("clayDraftStore.retireLegacyOperationEvidenceTransfer", _ => true).SetVoidResult();
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(19, false, false));
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", call => call.Arguments.Count == 2).SetResult(true);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        var begin = context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true);
        if (failBegin)
            begin.SetException(new JSException("Хранилище недоступно"));
        else
            begin.SetResult("""{"version":1,"attemptId":"fixture","kind":"territory.move","status":"running","revision":11,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddScoped(_ => new ClayDraftService(new MoveAcknowledgement(context.JSInterop.JSRuntime)));
        var errorHandler = new FakeSqlErrorHandler();
        context.Services.AddSingleton<ISqlErrorHandler>(errorHandler);
        context.Services.AddScoped(_ => new DbManager("Server=fixture", errorHandler, () => database));
        context.Services.AddSingleton<AuthenticationStateProvider, MoveAuthentication>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddMudServices();
        context.Services.AddClayTree(new ConfigurationBuilder().Build());
        context.ComponentFactories.AddStub<InventoryMainMenu>();
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/TerritorialEntity/TerritorialEntity_search?fixture=1");
        var form = context.Render<TerritorialEntitySearch>();
        form.WaitForAssertion(() => Assert.Equal(2, form.FindComponent<ClayTreeView>().Instance.RootNodes.Count));
        return form;
    }

    private sealed class MoveAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed record MoveJournalRow(Guid AttemptId, int EmployeeId, string ConflictKey, string CommandKind,
        InventoryOperationState State, int? ResultEntityId, string[] Scopes)
    {
        internal bool Open => State is not (InventoryOperationState.Confirmed or InventoryOperationState.Rejected);
    }

    private sealed class MoveConnection : DbConnection
    {
        private readonly ScriptedConnection _reads;

        internal MoveConnection() => _reads = new ScriptedConnection(Reads, emulateEquipmentIdentity: false);
        internal Queue<ScriptEntry> Reads { get; } = [];
        internal Dictionary<Guid, MoveJournalRow> Journal { get; } = [];
        internal Dictionary<Guid, MoveJournalRow> LegacyEvidence { get; } = [];
        internal int ParentId { get; private set; } = 5;
        internal int Updates { get; private set; }
        internal int Commits { get; private set; }
        internal int LatestMoveReads { get; private set; }
        internal bool FailCommitAcknowledgement { get; init; }

        internal void QueueAuth()
        {
            Reads.Enqueue(Script.Rows(Script.Row(("Id", 4))));
            Reads.Enqueue(Script.Rows());
            Reads.Enqueue(Script.Rows(Script.Row(("TerritoryUpdate", true))));
        }

        internal void QueueMoveRights(bool canUpdate = true)
        {
            Reads.Enqueue(Script.Rows(Script.Row(("Id", 4))));
            Reads.Enqueue(Script.Rows(Script.Row(("TerritoryUpdate", canUpdate))));
        }

        internal void QueueRoots() => Reads.Enqueue(Script.Rows(
            Script.Row(("Id", 5), ("Text", "Старый родитель"), ("Type", 3), ("Left", 1), ("Right", 10)),
            Script.Row(("Id", 9), ("Text", "Новый родитель"), ("Type", 3), ("Left", 11), ("Right", 14))));

        internal void QueuePane(string name)
        {
            Reads.Enqueue(Script.Rows(Script.Row(("Id", 5))));
            Reads.Enqueue(Script.Rows(Script.Row(("Id", 5), ("Name", name), ("Type", 3))));
        }

        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => _reads.State;
        public override void Open() => _reads.Open();
        public override void Close() => _reads.Close();
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new MoveCommand(this, _reads.CreateCommand());
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            Assert.Equal(IsolationLevel.Serializable, level);
            return new MoveTransaction(this);
        }

        private sealed class MoveTransaction(MoveConnection owner) : DbTransaction
        {
            internal MoveJournalRow? Entry { get; set; }
            internal bool Legacy { get; set; }
            internal int? StagedParentId { get; set; }
            public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
            protected override DbConnection DbConnection => owner;
            public override void Commit()
            {
                if (Entry is { } entry) (Legacy ? owner.LegacyEvidence : owner.Journal)[entry.AttemptId] = entry;
                if (StagedParentId is not { } parent) return;
                owner.ParentId = parent;
                owner.Commits++;
                if (owner.FailCommitAcknowledgement) throw new IOException("Ответ COMMIT потерян");
            }
            public override void Rollback() { }
        }

        private sealed class MoveCommand(MoveConnection owner, DbCommand inner) : DbCommand
        {
            public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
            public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
            public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
            public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
            public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection => inner.Parameters;
            protected override DbParameter CreateDbParameter() => inner.CreateParameter();
            public override void Prepare() => inner.Prepare();
            public override void Cancel() => inner.Cancel();

            private T Parameter<T>(string name) => Assert.IsType<T>(Parameters[name].Value);
            private bool IsJournal => CommandText.Contains("dbo.ClayzorOperationJournal", StringComparison.Ordinal) ||
                CommandText.Contains("dbo.ClayzorOperationScope", StringComparison.Ordinal) ||
                CommandText.Contains("dbo.ClayzorLegacyOperationEvidence", StringComparison.Ordinal);
            private bool IsLockedRead => CommandText.Contains("WITH (UPDLOCK,HOLDLOCK)", StringComparison.Ordinal);
            private MoveTransaction Transaction => Assert.IsType<MoveTransaction>(DbTransaction);

            public override object? ExecuteScalar()
            {
                if (CommandText.Contains("INSERT INTO dbo.ClayzorOperationScope", StringComparison.Ordinal) ||
                    CommandText.Contains("INSERT INTO dbo.ClayzorLegacyOperationScope", StringComparison.Ordinal))
                {
                    var entry = Transaction.Entry!;
                    Transaction.Entry = entry with { Scopes = [.. entry.Scopes, Parameter<string>("ConflictKey")] };
                    return 1;
                }
                if (CommandText.Contains("UPDATE dbo.ClayzorOperationScope", StringComparison.Ordinal))
                    return owner.Journal[Parameter<Guid>("AttemptId")].Scopes.Length;
                Assert.Contains("UPDATE dbo.ClayzorOperationJournal", CommandText);
                var id = Parameter<Guid>("AttemptId");
                var record = owner.Journal[id] with
                {
                    State = (InventoryOperationState)Parameter<byte>("NextState"),
                    ResultEntityId = Parameters["ResultEntityId"].Value as int?,
                };
                if (DbTransaction is MoveTransaction transaction)
                {
                    if (record.State == InventoryOperationState.Confirmed) Assert.NotNull(transaction.StagedParentId);
                    transaction.Entry = record;
                }
                else
                    owner.Journal[id] = record;
                return 1;
            }

            public override int ExecuteNonQuery()
            {
                if (CommandText.Contains("INSERT INTO dbo.ClayzorOperationJournal", StringComparison.Ordinal))
                {
                    var id = Parameter<Guid>("AttemptId");
                    Transaction.Entry = new(id, Parameter<int>("EmployeeId"), Parameter<string>("ConflictKey"),
                        Parameter<string>("CommandKind"), InventoryOperationState.Reserved, null, []);
                    return 1;
                }

                Assert.StartsWith("UPDATE dbo.Территории SET Parent=", CommandText);
                Assert.Equal(8, Parameter<int>("Id"));
                Transaction.StagedParentId = Parameter<int>("ParentId");
                owner.Updates++;
                return 1;
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (IsJournal) return ReadJournal();
                if (!IsLockedRead) return inner.ExecuteReader(behavior);
                var rows = new DataTable();
                foreach (var name in new[] { "Id", "ParentId", "Left", "Right" }) rows.Columns.Add(name, typeof(int));
                switch (Parameter<int>("Id"))
                {
                    case 8: rows.Rows.Add(8, Transaction.StagedParentId ?? owner.ParentId, 2, 3); break;
                    case 9: rows.Rows.Add(9, DBNull.Value, 11, 14); break;
                    default: throw new InvalidOperationException("Неожиданный ID");
                }
                return rows.CreateDataReader();
            }

            private DbDataReader ReadJournal()
            {
                if (CommandText == TerritorialEntitySql.LatestOwnMove)
                {
                    owner.LatestMoveReads++;
                    var current = owner.Journal.Values.Reverse().Concat(owner.LegacyEvidence.Values.Reverse());
                    var latest = current.Where(row => row.EmployeeId == Parameter<int>("EmployeeId") &&
                        row.ConflictKey == Parameter<string>("ConflictKey") && row.CommandKind == Parameter<string>("CommandKind"))
                        .OrderByDescending(row => row.Open).FirstOrDefault();
                    var moves = new DataTable();
                    moves.Columns.Add("AttemptId", typeof(Guid));
                    moves.Columns.Add("EmployeeId", typeof(int));
                    moves.Columns.Add("ConflictKey", typeof(string));
                    moves.Columns.Add("CommandKind", typeof(string));
                    moves.Columns.Add("State", typeof(byte));
                    moves.Columns.Add("ResultEntityId", typeof(int));
                    moves.Columns.Add("RelatedScope", typeof(string));
                    if (latest is not null)
                        foreach (var scope in latest.Scopes.DefaultIfEmpty(null))
                            moves.Rows.Add(latest.AttemptId, latest.EmployeeId, latest.ConflictKey, latest.CommandKind,
                                (byte)latest.State, (object?)latest.ResultEntityId ?? DBNull.Value, (object?)scope ?? DBNull.Value);
                    return moves.CreateDataReader();
                }
                if (CommandText.Contains("INSERT INTO dbo.ClayzorLegacyOperationEvidence", StringComparison.Ordinal))
                {
                    var id = Parameter<Guid>("AttemptId");
                    Transaction.Legacy = true;
                    Transaction.Entry = new(id, Parameter<int>("EmployeeId"), Parameter<string>("ConflictKey"),
                        Parameter<string>("CommandKind"), InventoryOperationState.Unknown, null, []);
                    return new ScriptedDataReader([Script.Row(("Value", true))]);
                }
                if (CommandText.Contains("WHERE ConflictKey = @ConflictKey AND OpenSlot = @OpenSlot", StringComparison.Ordinal))
                    return new ScriptedDataReader([Script.Row(("Value", owner.Journal.Values.Concat(owner.LegacyEvidence.Values).Any(row =>
                        row.Open && (row.ConflictKey == Parameter<string>("ConflictKey") ||
                            row.Scopes.Contains(Parameter<string>("ConflictKey"))))))]);
                if (CommandText.Contains("FROM dbo.ClayzorOperationScope", StringComparison.Ordinal))
                    return new ScriptedDataReader(owner.Journal[Parameter<Guid>("AttemptId")].Scopes
                        .Select(scope => Script.Row(("ConflictKey", scope))).ToList());
                Assert.Contains("WHERE AttemptId = @AttemptId", CommandText);
                var table = new DataTable();
                table.Columns.Add("AttemptId", typeof(Guid));
                table.Columns.Add("EmployeeId", typeof(int));
                table.Columns.Add("ConflictKey", typeof(string));
                table.Columns.Add("CommandKind", typeof(string));
                table.Columns.Add("State", typeof(byte));
                table.Columns.Add("ResultEntityId", typeof(int));
                if (owner.Journal.TryGetValue(Parameter<Guid>("AttemptId"), out var row))
                    table.Rows.Add(row.AttemptId, row.EmployeeId, row.ConflictKey, row.CommandKind, (byte)row.State,
                        (object?)row.ResultEntityId ?? DBNull.Value);
                return table.CreateDataReader();
            }
        }
    }
}
