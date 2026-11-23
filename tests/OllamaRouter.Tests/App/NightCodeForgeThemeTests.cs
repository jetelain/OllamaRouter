using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #170: proves the NightCodeForge Obsidian &amp; Indigo theme
/// (<c>src/OllamaRouter.App/Themes/NightCodeForgeTheme.axaml</c>) loads at RUNTIME from
/// <c>avares://ollamarouter-app/Themes/NightCodeForgeTheme.axaml</c> through the shared
/// <see cref="HeadlessThemeHelper"/> (the same dictionary <c>App.axaml</c> merges in production),
/// that its key design tokens resolve to the exact companion-app palette, and that
/// <c>{DynamicResource}</c> references resolve end-to-end through a real compiled view
/// (<see cref="MonitorView"/>'s TARGET table headers + the savings cards' target-identity
/// border brushes). The avares load + precompiled-XAML runtime load path is exercised
/// end-to-end because the helper only carries a URI — no programmatic token mirror of the
/// theme dictionary itself. All Avalonia property reads stay inside the headless session
/// dispatch (thread-affinity — brushes own the session's dispatcher thread). Kept in the
/// exclusive "Avalonia" xunit collection (the <see cref="AddTargetDialogsLayoutTests"/>
/// IClassFixture pattern).
/// </summary>
[Collection("Avalonia")]
public sealed class NightCodeForgeThemeTests : IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>
{
    private readonly PreloadDialogLayoutTests.HeadlessSessionFixture _fixture;

    public NightCodeForgeThemeTests(PreloadDialogLayoutTests.HeadlessSessionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Theme_AvaresDictionary_LoadsAndResolvesKeyTokens()
    {
        await _fixture.DispatchAsync(() =>
        {
            var dialog = new PreloadDialog(new PreloadDialogViewModel("theme-model", () => new FakeRouterApiClient()));
            HeadlessThemeHelper.Apply(dialog);

            // The NightCodeForge raw palette (design tokens §1 of the task spec).
            AssertBrushColor(dialog, "NightCodeBgCanvas", "#0B0D13");
            AssertBrushColor(dialog, "NightCodeBgSurface", "#181B24");

            // The Semi.Avalonia semantic overrides.
            AssertBrushColor(dialog, "SemiColorPrimary", "#6366F1");
            AssertBrushColor(dialog, "SemiColorSuccess", "#10B981");
            AssertBrushColor(dialog, "NightCodeAccentCrimsonLightText", "#F87171");
            AssertBrushColor(dialog, "SemiColorBorder", "#2D3345");

            // The four App.axaml Target*Brush mirrors (helper-injected — in production they
            // live in Application.Resources, which unshown headless windows never reach).
            AssertBrushColor(dialog, "TargetRemoteBrush", "#34E0A1");
            AssertBrushColor(dialog, "TargetCloudBrush", "#C48BFF");

            // The radius token (the only non-brush key under test).
            Assert.True(dialog.TryFindResource("SemiBorderRadiusMedium", out var radiusValue),
                "SemiBorderRadiusMedium did not resolve from the merged theme dictionary");
            var corner = Assert.IsType<CornerRadius>(radiusValue);
            Assert.Equal(new CornerRadius(8), corner);
        });
    }

    [Fact]
    public async Task Theme_AvaresUri_LoadsTheCompiledResourceDictionary()
    {
        await _fixture.DispatchAsync(() =>
        {
            // Force the avares load DIRECTLY (no programmatic fallback possible on this path):
            // the precompiled XAML of Themes/NightCodeForgeTheme.axaml must resolve through
            // the exact production URI App.axaml merges — this is the end-to-end proof of the
            // avares + precompiled-XAML runtime load path (task #170 spec §7.2 fallback).
            var include = new ResourceInclude((Uri?)null) { Source = HeadlessThemeHelper.ThemeSource };
            var loaded = (ResourceDictionary)include.Loaded;

            Assert.True(loaded.TryGetResource("NightCodeBgCanvas", null, out var canvas),
                "NightCodeBgCanvas missing from the avares-loaded theme dictionary");
            Assert.Equal(Color.Parse("#0B0D13"),
                Assert.IsAssignableFrom<ISolidColorBrush>(canvas).Color);

            Assert.True(loaded.TryGetResource("SemiColorSuccess", null, out var success),
                "SemiColorSuccess missing from the avares-loaded theme dictionary");
            Assert.Equal(Color.Parse("#10B981"),
                Assert.IsAssignableFrom<ISolidColorBrush>(success).Color);

            Assert.True(loaded.TryGetResource("SemiBorderRadiusMedium", null, out var radius),
                "SemiBorderRadiusMedium missing from the avares-loaded theme dictionary");
            Assert.Equal(new CornerRadius(8), Assert.IsType<CornerRadius>(radius));
        });
    }

    [Fact]
    public async Task MonitorView_DynamicResources_ResolveThroughTheMergedTheme()
    {
        await _fixture.DispatchAsync(() =>
        {
            var view = new MonitorView();
            HeadlessThemeHelper.Apply(view);
            var all = AllControls(view).ToList();

            // Every TARGET table header (Fleet / Statistics / In-Progress / Recent share the
            // label) carries {DynamicResource SemiColorText2} and resolves to #94A3B8 —
            // proof the whole compiled subtree resolves through the view's own Resources.
            var targetHeaders = all.OfType<TextBlock>().Where(tb => tb.Text == "TARGET").ToList();
            Assert.NotEmpty(targetHeaders);
            Assert.All(targetHeaders, tb =>
            {
                var brush = Assert.IsAssignableFrom<ISolidColorBrush>(tb.Foreground);
                Assert.Equal(Color.Parse("#94A3B8"), brush.Color);
            });

            // The savings cards' target-identity borders resolve the Target*Brush mirrors:
            // the Actual 7-Day Savings card (#34E0A1) and the Cloud Overflow Cost card (#C48BFF).
            var borderColors = all.OfType<Border>()
                .Select(b => b.BorderBrush)
                .OfType<ISolidColorBrush>()
                .Select(b => b.Color)
                .ToList();
            Assert.Contains(Color.Parse("#34E0A1"), borderColors);
            Assert.Contains(Color.Parse("#C48BFF"), borderColors);
        });
    }

    private static void AssertBrushColor(IResourceHost host, string key, string expectedHex)
    {
        Assert.True(host.TryFindResource(key, out var value),
            $"{key} did not resolve from the merged NightCodeForge theme dictionary");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        Assert.Equal(Color.Parse(expectedHex), brush.Color);
    }

    /// <summary>
    /// Walks the <em>content</em> tree (Panel children, Decorator child, ContentControl
    /// content), not the visual tree — visual children only materialize under a themed
    /// TopLevel (the <see cref="MonitorViewLayoutTests"/> walker).
    /// </summary>
    private static IEnumerable<Control> AllControls(Control root)
    {
        yield return root;

        var children = new List<Control>();
        if (root is Panel panel)
            children.AddRange(panel.Children);
        if (root is Decorator { Child: not null } decorator)
            children.Add(decorator.Child);
        if (root is ContentControl { Content: Control content })
            children.Add(content);

        foreach (var child in children.Distinct())
        {
            foreach (var descendant in AllControls(child))
                yield return descendant;
        }
    }
}
