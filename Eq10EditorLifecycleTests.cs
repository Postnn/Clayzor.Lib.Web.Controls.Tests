using System.Text.Json;
using Bunit;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Production lifecycle typed child state без ручного Capture как условия autosave.</summary>
public sealed class Eq10EditorLifecycleTests
{
    /// <summary>UI add/remove автоматически публикует структуру, а restore не воскрешает удалённый HD.</summary>
    [Fact]
    public async Task ComputerUiChanges_AutoPersistAndRestoreWithoutDeletedHd()
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var published = new List<EquipmentEditorDraftChange>();
        var signal = new SemaphoreSlim(0);
        var editorContext = new EquipmentEditorContext(7, 1, () => true)
        {
            DraftChanged = change =>
            {
                lock (published)
                    published.Add(change);
                signal.Release();
                return Task.CompletedTask;
            },
        };
        var reference = await ShowComputerAsync(context, editorContext);
        var dialog = provider.FindComponent<EquipmentComputerDialog>();
        await WaitForPublishAsync(signal, published, change => change.Revision >= 1);
        Assert.Equal(0, CountArray(published[^1].State, "$hd"));

        await ClickClayButtonAsync(dialog, "Добавить HD");
        await WaitForPublishAsync(signal, published, change => CountArray(change.State, "$hd") == 1);
        await ClickClayButtonAsync(dialog, "Добавить HD");
        await WaitForPublishAsync(signal, published, change => CountArray(change.State, "$hd") == 2);
        await ClickClayButtonAsync(dialog, "Удалить компонент HD");
        await WaitForPublishAsync(signal, published, change =>
            CountArray(change.State, "$hd") == 1 && CountArray(change.State, "$hdDeleted") == 1);

        EquipmentEditorDraftChange latest;
        lock (published)
            latest = published[^1];
        Assert.Equal(1, JsonDocument.Parse(latest.State).RootElement.GetProperty("$hdSlots")[0].GetInt32());
        Assert.Equal(0, JsonDocument.Parse(latest.State).RootElement.GetProperty("$hdDeleted")[0].GetInt32());
        var publishedCount = published.Count;
        editorContext.Invalidate();
        await dialog.FindAll("textarea").Last().InputAsync("late-input");
        await Task.Delay(100);
        Assert.Equal(publishedCount, published.Count);
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));

        await using var restoredContext = CreateContext();
        var restoredProvider = restoredContext.Render<MudDialogProvider>();
        var restoredEditorContext = new EquipmentEditorContext(7, 2, () => true) { DraftState = latest.State };
        await ShowComputerAsync(restoredContext, restoredEditorContext);
        var restored = restoredProvider.FindComponent<EquipmentComputerDialog>();

        Assert.Single(restored.Instance.Hd);
        Assert.Equal(1, Assert.Single(restored.Instance.HdSlots));
        Assert.Equal(0, Assert.Single(restored.Instance.DeletedHdSlots));
        var rawSlots = restored.FindAll("input[data-draft-key='computer.hd']");
        Assert.Equal(2, rawSlots.Count);
        Assert.True(rawSlots[0].HasAttribute("disabled"));
        Assert.False(rawSlots[1].HasAttribute("disabled"));
    }

    /// <summary>Одно медленное Persist не теряет промежуточные изменения и не запускает параллельные записи.</summary>
    [Fact]
    public async Task DraftPublisher_CoalescesToLatestRevision()
    {
        var editorContext = new EquipmentEditorContext(7, 1, () => true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<string>();
        var concurrent = 0;
        var maxConcurrent = 0;
        editorContext.DraftChanged = async change =>
        {
            concurrent++;
            maxConcurrent = Math.Max(maxConcurrent, concurrent);
            if (change.Revision == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
            states.Add(change.State);
            concurrent--;
        };

        var first = editorContext.PublishDraftStateAsync("one");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = editorContext.PublishDraftStateAsync("two");
        var third = editorContext.PublishDraftStateAsync("three");
        release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["one", "three"], states);
        Assert.Equal(1, maxConcurrent);
        editorContext.Invalidate();
        await editorContext.PublishDraftStateAsync("stale");
        Assert.Equal(2, states.Count);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        return context;
    }

    private static Task<IDialogReference> ShowComputerAsync(BunitContext context, EquipmentEditorContext editorContext) =>
        context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentComputerDialog>(
            "Computer", new DialogParameters<EquipmentComputerDialog>
            {
                { dialog => dialog.EditorContext, editorContext },
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.Date, new DateTime(2026, 11, 20) },
                { dialog => dialog.Today, new DateTime(2026, 11, 20) },
                { dialog => dialog.MemoryBanks, 0 },
            });

    private static async Task ClickClayButtonAsync(IRenderedComponent<EquipmentComputerDialog> dialog, string text)
    {
        var button = dialog.FindComponents<Clayzor.Lib.Web.Controls.Components.ClayButton>()
            .First(component => component.Instance.Text == text);
        await button.Find("button").ClickAsync();
    }

    private static async Task WaitForPublishAsync(
        SemaphoreSlim signal,
        List<EquipmentEditorDraftChange> published,
        Func<EquipmentEditorDraftChange, bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < timeout)
        {
            lock (published)
            {
                if (published.Any(predicate))
                    return;
            }
            await signal.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        lock (published)
            Assert.True(published.Any(predicate));
    }

    private static int CountArray(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty(name).GetArrayLength();
}
