using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App.Converters;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Main-tab error banner contract tests (task #173): the <c>LastError</c> surface of EVERY
/// tab (Monitor / Agents &amp; Sessions / Fleet / Cartography / Settings) is the
/// NightCodeForge <c>SemiColorDangerLight</c> rounded banner (task #171 pattern) — the
/// FIRST child of the tab's root <see cref="StackPanel"/> is a <see cref="Border"/> with
/// <c>CornerRadius 8</c>, <c>IsVisible</c> ← <c>IsErrorVisible</c>, and its single inner
/// <see cref="TextBlock"/> binds <c>LastError</c> (a regression to the retired bare red
/// TextBlock fails the <c>IsType&lt;Border&gt;</c> pin). Also pins the task #173 additions:
/// the Monitor fleet-row and Fleet identity-row liveness cells route their Foreground
/// through the <see cref="LivenessToBrushConverter"/>, and the Settings API-key rows are
/// <c>NightCodeBgInput</c> sub-cards (<c>CornerRadius 6</c> <see cref="Border"/> roots).
/// Direct (session-less) <see cref="UserControl"/> construction — the same pattern as
/// <see cref="MonitorViewLayoutTests"/> — with the <see cref="HeadlessThemeHelper"/>
/// fallback-safe theme application.
/// </summary>
[Collection("Avalonia")]
public sealed class MainTabErrorBannerLayoutTests
{
    [Fact]
    public void MonitorView_ErrorBanner_IsFirstChildOfRootStackPanel()
    {
        AssertErrorBanner(new MonitorView(), "MonitorView");
    }

    [Fact]
    public void AgentSessionsView_ErrorBanner_IsFirstChildOfRootStackPanel()
    {
        AssertErrorBanner(new AgentSessionsView(), "AgentSessionsView");
    }

    [Fact]
    public void FleetTargetsView_ErrorBanner_IsFirstChildOfRootStackPanel()
    {
        AssertErrorBanner(new FleetTargetsView(), "FleetTargetsView");
    }

    [Fact]
    public void CartographyView_ErrorBanner_IsFirstChildOfRootStackPanel()
    {
        AssertErrorBanner(new CartographyView(), "CartographyView");
    }

    [Fact]
    public void SettingsView_ErrorBanner_IsFirstChildOfRootStackPanel()
    {
        AssertErrorBanner(new SettingsView(), "SettingsView");
    }

    [Fact]
    public void MonitorView_FleetRowLivenessCell_IsColoredByTheLivenessConverter()
    {
        var view = new MonitorView();
        HeadlessThemeHelper.Apply(view);
        view.ApplyTemplate();

        var livenessCell = BuildLivenessCell(
            view,
            "FleetRows",
            2, // the LIVENESS column of the fleet row grid
            new MonitorFleetRow(
                "probe", true, "Online", 0, null,
                Array.Empty<string>(), "local", "http://probe:11434"));

        Assert.Equal("Liveness", BindingPath(livenessCell, TextBlock.TextProperty));
        Assert.True(
            ReferenceEquals(
                BindingConverter(livenessCell, TextBlock.ForegroundProperty),
                LivenessToBrushConverter.Instance),
            "the fleet LIVENESS cell must route its Foreground through the LivenessToBrushConverter");
    }

    [Fact]
    public void FleetTargetsView_IdentityRowLivenessCell_IsColoredByTheLivenessConverter()
    {
        var view = new FleetTargetsView();
        HeadlessThemeHelper.Apply(view);
        view.ApplyTemplate();

        var livenessCell = BuildLivenessCell(
            view,
            "Rows",
            1, // the liveness cell column of the identity row grid
            new FleetTargetRow { Name = "probe", Liveness = "Online" });

        Assert.Equal("Liveness", BindingPath(livenessCell, TextBlock.TextProperty));
        Assert.True(
            ReferenceEquals(
                BindingConverter(livenessCell, TextBlock.ForegroundProperty),
                LivenessToBrushConverter.Instance),
            "the fleet identity-row liveness cell must route its Foreground through the LivenessToBrushConverter");
    }

    [Fact]
    public void SettingsView_ApiKeyRows_AreNightCodeBgInputSubCards()
    {
        var view = new SettingsView();
        HeadlessThemeHelper.Apply(view);
        view.ApplyTemplate();

        var itemsControl = AllControls(view).OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == "ApiKeys");
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        var card = Assert.IsType<Border>(template.Build(new object()));

        Assert.Equal(new CornerRadius(6), card.CornerRadius);
        var grid = Assert.IsType<Grid>(card.Child);

        // The four key-cell bindings survive inside the sub-card.
        var texts = grid.Children.OfType<TextBlock>().ToArray();
        Assert.Equal(3, texts.Length);
        Assert.Equal("Name", BindingPath(texts[0], TextBlock.TextProperty));
        Assert.Equal("CreatedAt", BindingPath(texts[1], TextBlock.TextProperty));
        Assert.Equal("ExpiresAt", BindingPath(texts[2], TextBlock.TextProperty));
        Assert.Single(grid.Children.OfType<Button>());
    }

    // ── Shared helpers ──

    /// <summary>
    /// The tab root: <see cref="UserControl.Content"/> → <see cref="ScrollViewer"/> →
    /// <see cref="StackPanel"/>; asserts the banner pins above.
    /// </summary>
    private static void AssertErrorBanner(UserControl view, string viewName)
    {
        HeadlessThemeHelper.Apply(view);
        view.ApplyTemplate();

        var scroll = Assert.IsType<ScrollViewer>(view.Content);
        var stack = Assert.IsType<StackPanel>(scroll.Content);
        var banner = Assert.IsType<Border>(stack.Children[0]);
        Assert.Equal(new CornerRadius(8), banner.CornerRadius);
        Assert.Equal("IsErrorVisible", BindingPath(banner, Control.IsVisibleProperty));

        var message = Assert.IsType<TextBlock>(banner.Child);
        Assert.Equal("LastError", BindingPath(message, TextBlock.TextProperty));
        Assert.True(message.TextWrapping == Avalonia.Media.TextWrapping.Wrap,
            $"{viewName} banner text must wrap");
    }

    /// <summary>
    /// Builds the row template of the <see cref="ItemsControl"/> bound to
    /// <paramref name="itemsSourcePath"/> off-tree and returns the LIVENESS
    /// <see cref="TextBlock"/> at grid column <paramref name="column"/>. The template root
    /// varies (bare <see cref="Grid"/> for the Monitor fleet row; a <see cref="Border"/> card
    /// for the fleet target row), so the cell is located by walking the LOGICAL tree of the
    /// built template and matching column + <c>Liveness</c> text path.
    /// </summary>
    private static TextBlock BuildLivenessCell(
        UserControl view, string itemsSourcePath, int column, object sampleRow)
    {
        var itemsControl = AllControls(view).OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == itemsSourcePath);
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        // NOTE: the built root is NOT always a Control (the fleet target row root is a
        // Border = plain Decorator in Avalonia 12) — walk from an AvaloniaObject.
        var built = Assert.IsAssignableFrom<AvaloniaObject>(template.Build(sampleRow));

        var nodes = new List<AvaloniaObject> { built };
        var queue = new Queue<AvaloniaObject>(nodes);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var child in ((ILogical)node).GetLogicalChildren())
            {
                nodes.Add((AvaloniaObject)child);
                queue.Enqueue((AvaloniaObject)child);
            }
        }

        var cells = nodes
            .OfType<TextBlock>()
            .Where(tb => Grid.GetColumn(tb) == column &&
                         BindingPath(tb, TextBlock.TextProperty) == "Liveness")
            .ToList();
        return Assert.Single(cells);
    }

    private static string? BindingPath(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Description")?.GetValue(expression) as string;
    }

    /// <summary>
    /// Reads the (internal) binding expression's <c>Converter</c> reflectively — the
    /// compiled XAML binding carries the <c>{x:Static …}</c> converter instance on it.
    /// </summary>
    private static object? BindingConverter(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Converter")?.GetValue(expression);
    }

    /// <summary>
    /// Walks the <em>content</em> tree (Panel children, Decorator child, ContentControl
    /// content) — visual children only materialize under a themed TopLevel.
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
            {
                yield return descendant;
            }
        }
    }
}
