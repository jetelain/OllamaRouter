using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Fleet &amp; Cloud Targets tab layout contract tests (task #148, re-baselined by the clean-card
/// restructure; the 'Update hardware' busy-state contract dates to task #144): the row
/// DataTemplate is split into an IDENTITY row (name / liveness / Monitor-parity enabled cluster /
/// action column with 'Update hardware' BEFORE Edit) and an ENDPOINT row (location tag / type /
/// provider / URL in the single star column, ellipsizing with a full-URL tooltip / key state /
/// ssh label) — every header element sits in its OWN Grid cell (no two children share a column),
/// so nothing can ever overlap at the 960px minimum window. The hardware profile section is
/// ALWAYS EXPANDED (no Expander), the enablement cluster carries an 'Enabled' label + display-only
/// CheckBox + 'Toggle' button (the word 'Persist' appears nowhere), and the effective-values strip
/// is a WrapPanel that also carries the resident-models count. Structural assertions only —
/// <see cref="FleetTargetsView"/> is a <see cref="UserControl"/>, so construction needs no
/// windowing platform (the <see cref="MonitorViewLayoutTests"/> precedent).
/// </summary>
/// <remarks>
/// [Collection("Avalonia")]: this class creates <see cref="FleetTargetsView"/> control trees,
/// so it must never run in parallel with the other Avalonia-control-creating test classes —
/// Avalonia 12 keeps process-wide mutable state (the thread-unsafe
/// AvaloniaPropertyDictionaryPool static stack and the first-touch-owned Dispatcher.UIThread),
/// which corrupts under concurrent control construction.
/// </remarks>
[Collection("Avalonia")]
public sealed class FleetTargetsViewLayoutTests
{
    [Fact]
    public void RowTemplate_NoPersistControlAnywhere()
    {
        var view = new FleetTargetsView();
        view.ApplyTemplate();

        // Task #148 (D1): the 'Persist' button was renamed to 'Toggle' — the word 'Persist'
        // must appear nowhere in the view OR in the built row template (the DataTemplate only
        // materializes when instantiated, so both trees are searched).
        var (row, _, _) = LoadRowTemplate();
        Assert.DoesNotContain(
            AllControls(view).Concat(AllControls(row)).OfType<Button>(),
            b => b.Content is string content && content == "Persist");
    }

    [Fact]
    public void RowTemplate_IdentityRow_EnabledCluster_MirrorsMonitor()
    {
        var (_, identity, _) = LoadRowTemplate();

        // The identity grid's enabled cluster (Grid.Column 2): 'Enabled' label + display-only
        // state CheckBox + 'Toggle' button — verbatim Monitor & Savings parity (D1).
        var cluster = Assert.IsType<StackPanel>(
            identity.Children.Single(c => Grid.GetColumn(c) == 2));

        // Label: the literal 'Enabled' caption in the monitor table-header style.
        var label = cluster.Children.OfType<TextBlock>().Single(tb => tb.Text == "Enabled");
        Assert.Equal(11.0, label.FontSize);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, label.FontWeight);

        // State display: the CheckBox binds 'Enabled' and stays display-only (not hit-testable,
        // so it can never steal the toggle click — same as the Monitor ENABLED column).
        var checkBox = cluster.Children.OfType<CheckBox>().Single();
        Assert.Equal("Enabled", BindingPath(checkBox, CheckBox.IsCheckedProperty));
        Assert.False(checkBox.IsHitTestVisible,
            "the enabled-state CheckBox must stay display-only (IsHitTestVisible=false)");

        // Interactive toggle: literal 'Toggle' content, the row as Tag, the persisted-flag
        // tooltip, and monitor-parity sizing (Padding 8,2 / FontSize 11). CheckBox derives
        // from ButtonBase (not Button) in Avalonia 12; the defensive filter keeps this robust.
        var toggle = cluster.Children.OfType<Button>().Single(b => b is not CheckBox);
        Assert.Equal("Toggle", toggle.Content as string);
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(toggle, Control.TagProperty));
        Assert.Equal(
            "Toggle the persisted (config) enablement flag",
            toggle.GetValue(ToolTip.TipProperty));
        Assert.Equal(11.0, toggle.FontSize);
        Assert.Equal(new Avalonia.Thickness(8, 2, 8, 2), toggle.Padding);
    }

    [Fact]
    public void RowTemplate_EndpointRow_OwnCellPerElement()
    {
        var (_, _, endpoint) = LoadRowTemplate();

        // Location tag (col 0) in its TagBorder-themed Border.
        var tag = Assert.IsType<Border>(endpoint.Children.Single(c => Grid.GetColumn(c) == 0));
        var tagPanel = Assert.IsType<StackPanel>(tag.Child);
        Assert.Equal(2, tagPanel.Children.OfType<TextBlock>().Count()); // emoji + name

        // Type (col 1), provider (col 2) — plain text cells.
        Assert.Equal("Type", BindingPath(
            Assert.IsType<TextBlock>(endpoint.Children.Single(c => Grid.GetColumn(c) == 1)),
            TextBlock.TextProperty));
        Assert.Equal("Provider", BindingPath(
            Assert.IsType<TextBlock>(endpoint.Children.Single(c => Grid.GetColumn(c) == 2)),
            TextBlock.TextProperty));

        // URL (col 3, the star column): trims with ellipsis and tooltips the FULL url.
        var url = Assert.IsType<TextBlock>(endpoint.Children.Single(c => Grid.GetColumn(c) == 3));
        Assert.Equal("Url", BindingPath(url, TextBlock.TextProperty));
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, url.TextTrimming);
        Assert.Equal("Url", BindingPath(url, ToolTip.TipProperty));

        // Key state (col 4) and the ssh credential label (col 5) — each in its OWN cell, so
        // the old 'http://127.0.0.1:11435key:False' overlap is structurally impossible.
        Assert.Equal("ApiKeyConfigured", BindingPath(
            Assert.IsType<TextBlock>(endpoint.Children.Single(c => Grid.GetColumn(c) == 4)),
            TextBlock.TextProperty));
        var sshLabel = Assert.IsType<TextBlock>(endpoint.Children.Single(c => Grid.GetColumn(c) == 5));
        Assert.Equal("SshLabel", BindingPath(sshLabel, TextBlock.TextProperty));
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, sshLabel.TextTrimming);
        Assert.Equal("SshLabel", BindingPath(sshLabel, ToolTip.TipProperty));
    }

    [Fact]
    public void RowTemplate_HeaderGrids_NoSharedColumns_SingleStarColumn()
    {
        var (_, identity, endpoint) = LoadRowTemplate();

        // Overlap guard (TargetEditDialogLayoutTests.FormGrid_NoTwoChildren_ShareACell precedent):
        // in BOTH header grids no two children may share a Grid.Column — the original defect was
        // six info elements packed into one star-column StackPanel whose desired width overflowed.
        foreach (var (name, grid) in new[] { ("identity row", identity), ("endpoint row", endpoint) })
        {
            var columns = grid.Children.Select(Grid.GetColumn).ToArray();
            Assert.True(columns.Length == columns.Distinct().Count(),
                $"{name}: two children share a Grid.Column, which re-opens the overlap defect");

            var starCount = grid.ColumnDefinitions.Count(d => d.Width.IsStar);
            Assert.True(starCount == 1,
                $"{name}: grid declares {starCount} star columns, expected exactly one");
        }

        // The single star column IS the flexible one: the Name column (identity col 0) and the
        // URL column (endpoint col 3).
        Assert.True(identity.ColumnDefinitions[0].Width.IsStar, "identity Name column is not the star column");
        Assert.True(endpoint.ColumnDefinitions[3].Width.IsStar, "endpoint URL column is not the star column");
    }

    [Fact]
    public void RowTemplate_HardwareProfile_AlwaysVisible_NoExpander()
    {
        var (row, _, _) = LoadRowTemplate();

        // Task #148: the profile is ALWAYS EXPANDED — no collapsing container anywhere in the row.
        Assert.Empty(AllControls(row).OfType<Expander>());

        // The section header TextBlock (former Expander header) is a direct row-section child.
        var header = AllControls(row).OfType<TextBlock>().Single(tb =>
            BindingPath(tb, TextBlock.TextProperty) == "HardwareSource");
        // ...and the section that owns it is a direct child of the row StackPanel.
        var section = Assert.IsType<StackPanel>(header.Parent);
        Assert.Contains(section, row.Children);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, header.FontWeight);

        // The content bindings + the GPU list exist in the template (unconditionally parseable).
        var allText = AllControls(row).OfType<TextBlock>();
        foreach (var expected in new[]
        {
            "HardwareReachable", "HardwareOs", "HardwareArchitecture",
            "HardwareSystemRamMB", "HardwareTotalFastMemoryMB", "HardwareEffectiveBandwidthGBs",
        })
        {
            // Single: asserts the line exists exactly once (the hardware section lost it otherwise).
            var line = allText.Single(tb => BindingPath(tb, TextBlock.TextProperty) == expected);
            Assert.NotNull(line);
        }
        var gpus = AllControls(row).OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == "Gpus");
        Assert.NotNull(gpus);
    }

    [Fact]
    public void RowTemplate_ActionColumn_ContainsUpdateHardwareButtonBeforeEdit()
    {
        var (_, identity, _) = LoadRowTemplate();

        // The identity grid's action column (Grid.Column 3) holds the button row.
        var actionPanel = Assert.IsType<StackPanel>(
            identity.Children.Single(c => Grid.GetColumn(c) == 3));
        var buttons = actionPanel.Children.OfType<Button>().ToArray();

        // The 'Update hardware' button carries the BUSY-STATE binding (not a literal
        // Content); Edit / Delete keep their literal content (the task #102 row actions).
        var updateHardware = buttons.Single(b =>
            BindingPath(b, Button.ContentProperty) == "UpdateHardwareButtonContent");
        var edit = buttons.Single(b => b.Content is string s && s == "Edit");
        var delete = buttons.Single(b => b.Content is string s && s == "Delete");

        // Right-hand action column, BEFORE Edit (maintainer-confirmed placement).
        Assert.True(Array.IndexOf(buttons, updateHardware) < Array.IndexOf(buttons, edit),
            "the 'Update hardware' button must precede Edit in the row action column");
        Assert.True(Array.IndexOf(buttons, edit) < Array.IndexOf(buttons, delete),
            "Edit must still precede Delete in the row action column");

        // The transient busy-state content + the ssh-probeable gating binding.
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(updateHardware, Button.ContentProperty));
        Assert.Equal("UpdateHardwareButtonContent", BindingPath(updateHardware, Button.ContentProperty));
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(updateHardware, Button.IsEnabledProperty));
        Assert.Equal("CanUpdateHardware", BindingPath(updateHardware, Button.IsEnabledProperty));
        // The tooltip is a LITERAL (not a binding) — assert its value directly.
        Assert.Equal(
            "Re-run the daemon-side hardware probe for this target (overwrites the stored profile)",
            updateHardware.GetValue(ToolTip.TipProperty));
    }

    [Fact]
    public void RowTemplate_EffectiveValues_WrapPanel_KeepsAllChips_IncludesResidentCount()
    {
        var (row, _, _) = LoadRowTemplate();

        // Task #148: the effective-values strip is a WrapPanel (the old horizontal StackPanel
        // had the same overflow class at narrow widths), and the resident-models count moved
        // here from the header as the LAST chip.
        var wrap = Assert.IsType<WrapPanel>(
            row.Children.OfType<WrapPanel>().Single());
        var chips = wrap.Children.OfType<TextBlock>().ToArray();

        var chipPaths = chips.Select(tb => BindingPath(tb, TextBlock.TextProperty)).ToArray();
        Assert.Equal(new[]
        {
            "SpeedFactor", "MaxVramMB", "MaxParallel",
            "EffectiveKeepAliveSeconds", "KeepAliveSource", "PowerWatts", "HardwareHint",
            "ResidentModels.Count",
        }, chipPaths);

        // The resident chip keeps its tooltip.
        Assert.Equal("Resident models",
            chips[^1].GetValue(ToolTip.TipProperty));
    }

    private static (StackPanel Row, Grid Identity, Grid Endpoint) LoadRowTemplate()
    {
        var view = new FleetTargetsView();
        view.ApplyTemplate();
        var all = AllControls(view).ToList();

        var itemsControl = all.OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == "Rows");
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        var built = template.Build(null);

        var row = Assert.IsType<StackPanel>(Assert.IsType<Border>(built).Child);
        // Row 1 (identity) is the first Grid child of the row StackPanel; row 2 (endpoint) is
        // the Grid child carrying the Url-bound TextBlock.
        var identity = row.Children.OfType<Grid>().First();
        var endpoint = row.Children.OfType<Grid>().Single(g =>
            g.Children.OfType<TextBlock>().Any(tb => BindingPath(tb, TextBlock.TextProperty) == "Url"));
        return (row, identity, endpoint);
    }

    /// <summary>
    /// Returns the XAML path of the binding on <paramref name="property"/>, or null when the
    /// property carries no binding. Avalonia 12's concrete <c>BindingExpression</c> type is
    /// internal, so the human-readable <c>Description</c> is read reflectively (the
    /// <see cref="MonitorViewLayoutTests"/> precedent).
    /// </summary>
    private static string? BindingPath(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Description")?.GetValue(expression) as string;
    }

    /// <summary>
    /// Walks the <em>content</em> tree (Panel children, Decorator child, ContentControl
    /// content), not the visual tree — visual children only materialize under a themed
    /// TopLevel.
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
