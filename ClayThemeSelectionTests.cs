using Clayzor.Lib.Web.Controls.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class ClayThemeSelectionTests
{
    [Theory]
    [InlineData(null, ClayThemePreference.ClassicLight)]
    [InlineData("", ClayThemePreference.ClassicLight)]
    [InlineData("unknown:dark", ClayThemePreference.ClassicLight)]
    [InlineData("legacy:dark", ClayThemePreference.LegacyLight)]
    [InlineData("corporate:dark", ClayThemePreference.CorporateLight)]
    [InlineData("modern:dark", ClayThemePreference.ModernLight)]
    [InlineData("modern:broken", ClayThemePreference.ClassicLight)]
    public void PreferenceFallback(string? stored, ClayThemePreference expected) =>
        Assert.Equal(expected, ClayThemeCatalog.Parse(stored));

    [Fact]
    public void FiveModesRoundTripWithIndependentMudThemes()
    {
        Assert.Equal(5, ClayThemeCatalog.Available.Count);
        foreach (var preference in ClayThemeCatalog.Available)
        {
            Assert.Equal(preference, ClayThemeCatalog.Parse(preference.Serialize()));
            var first = ClayTheme.Create(preference);
            var second = ClayTheme.Create(preference);
            Assert.NotSame(first.PaletteLight, second.PaletteLight);
            Assert.Equal(first.PaletteLight.TextPrimary, second.PaletteLight.TextPrimary);
            Assert.Equal(ClayTheme.Create().LayoutProperties.DefaultBorderRadius, first.LayoutProperties.DefaultBorderRadius);
            Assert.Equal(ClayTheme.Create().Typography.Default.FontSize, first.Typography.Default.FontSize);
        }

        var original = ClayTheme.Create();
        Assert.Equal(original.PaletteLight.Primary, ClayTheme.Create(ClayThemePreference.ClassicLight).PaletteLight.Primary);
        Assert.Equal(original.PaletteDark.Surface, ClayTheme.Create(ClayThemePreference.ClassicDark).PaletteDark.Surface);
        Assert.NotEqual(original.PaletteLight.Secondary, ClayTheme.Create(ClayThemePreference.ModernLight).PaletteLight.Secondary);
    }

    [Fact]
    public async Task MissingValueLoadsOnceAndNeverWritesDefault()
    {
        var store = new PreferenceStore();
        using var state = NewState(store);
        await state.InitializeAsync();
        await state.InitializeAsync();
        await state.SelectAsync(ClayThemePreference.ClassicLight);
        Assert.Equal(ClayThemePreference.ClassicLight, state.Preference);
        Assert.Equal(1, store.LoadCount);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task ExplicitLightBeforeReadCompletesWinsOverStoredDark()
    {
        var read = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PreferenceStore { OnLoad = _ => read.Task };
        using var state = NewState(store);
        var initialization = state.InitializeAsync();
        await state.SelectAsync(ClayThemePreference.ClassicLight);
        read.SetResult("classic:dark");
        await initialization;
        Assert.Equal(ClayThemePreference.ClassicLight, state.Preference);
        Assert.Equal(["classic:light"], store.Writes);
    }

    [Fact]
    public async Task LatestChoiceWinsAndDuplicateSelectionDoesNotWrite()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PreferenceStore { OnSave = (value, _) => value == "legacy:light" ? first.Task : Task.CompletedTask };
        using var state = NewState(store);
        await state.InitializeAsync();
        var legacy = state.SelectAsync(ClayThemePreference.LegacyLight);
        var corporate = state.SelectAsync(ClayThemePreference.CorporateLight);
        var modern = state.SelectAsync(ClayThemePreference.ModernLight);
        Assert.Equal(ClayThemePreference.ModernLight, state.Preference);
        Assert.Equal(["legacy:light"], store.Writes);

        first.SetResult();
        await Task.WhenAll(legacy, corporate, modern);
        await state.SelectAsync(ClayThemePreference.ModernLight);
        Assert.Equal(["legacy:light", "modern:light"], store.Writes);
        Assert.Equal(ClayThemePreference.ModernLight, state.Preference);
    }

    [Fact]
    public async Task ReadAndWriteErrorsPreserveEditableThemeAndAllowExplicitRetry()
    {
        var store = new PreferenceStore
        {
            OnLoad = _ => throw new InvalidOperationException("Read failure"),
            OnSave = (_, _) => throw new InvalidOperationException("Write failure"),
        };
        using var state = NewState(store);
        await state.InitializeAsync();
        Assert.True(state.LoadFailed);
        Assert.Empty(store.Writes);
        await state.SelectAsync(ClayThemePreference.ClassicDark);
        Assert.True(state.SaveFailed);
        Assert.Equal(ClayThemePreference.ClassicDark, state.Preference);

        store.OnSave = (_, _) => Task.CompletedTask;
        await state.SelectAsync(ClayThemePreference.ClassicDark);
        Assert.False(state.SaveFailed);
        Assert.False(state.LoadFailed);
        Assert.Equal(["classic:dark", "classic:dark"], store.Writes);
    }

    [Fact]
    public async Task PreviewAndOtherCircuitHaveIndependentState()
    {
        var store = new PreferenceStore { OnLoad = _ => Task.FromResult<string?>("legacy:light") };
        using var regular = NewState(store);
        using var preview = NewState(null);
        await regular.InitializeAsync();
        await preview.InitializeAsync();
        await preview.SelectAsync(ClayThemePreference.CorporateLight);
        Assert.Equal(ClayThemePreference.LegacyLight, regular.Preference);
        Assert.Equal(ClayThemePreference.CorporateLight, preview.Preference);
        Assert.Empty(store.Writes);
    }

    private static ClayThemeState NewState(PreferenceStore? store) => new(NullLogger<ClayThemeState>.Instance, store);

    private sealed class PreferenceStore : IClayThemePreferenceStore
    {
        public int LoadCount { get; private set; }
        public List<string> Writes { get; } = [];
        public Func<CancellationToken, Task<string?>> OnLoad { get; init; } = _ => Task.FromResult<string?>(null);
        public Func<string, CancellationToken, Task> OnSave { get; set; } = (_, _) => Task.CompletedTask;

        public Task<string?> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return OnLoad(cancellationToken);
        }

        public Task SaveAsync(string value, CancellationToken cancellationToken)
        {
            Writes.Add(value);
            return OnSave(value, cancellationToken);
        }
    }
}
