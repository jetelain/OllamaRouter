using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Fleet &amp; Cloud Targets tab layout contract tests (task #148, re-baselined by the clean-card
/// restructure and by the task #153 card streamline — the 'Update hardware' busy-state contract
/// dates to task #144): the row DataTemplate is split into an IDENTITY row (name / liveness /
/// ONE interactive 'Enabled' CheckBox (task #161 — the retired label + display-only CheckBox +
/// 'Toggle' button convention) / action column with 'Update hardware' BEFORE Edit) and a
/// FIVE-CELL endpoint row (location tag / type / provider / URL in the single star column,
/// ellipsizing with a full-URL tooltip / ssh label — the 'key: …' cell is gone). The effective
/// values strip is a WrapPanel with EXACTLY five chips (MaxParallel / HardwareHint / Power /
/// KeepAliveLabel / VramOverrideLabel), and the hardware profile section is ALWAYS EXPANDED
/// (no Expander) with one line per topic (OS / RAM+VRAM+bandwidth / GPU) — the cryptic chips
/// (SpeedFactor / MaxVramMB / Resident) and the 'Declared models' row appear nowhere.
/// The enablement cluster is exactly one interactive CheckBox with Content "Enabled" (the
/// word 'Persist' appears nowhere). Structural assertions only —
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

    // -- Task #149: the dedicated per-type add entry points ---------------------------

    [Fact]
    public void Toolbar_ExposesExactlyRefreshAndTheTwoDedicatedAddButtons()
    {
        var toolbar = GetToolbar();
        var buttons = toolbar.Children.OfType<Button>().ToArray();

        // EXACTLY three buttons: Refresh + the two dedicated add entry points (the single
        // generic 'Add Target' button is gone).
        Assert.Equal(new[] { "Refresh", "Add Ollama Node", "Add External Provider" },
            buttons.Select(b => b.Content as string).ToArray());

        // Refresh stays command-bound; the add buttons are code-behind handlers (they open
        // the dedicated dialogs) and carry the primary style.
        var refresh = buttons.Single(b => (string)b.Content! == "Refresh");
        Assert.Equal("RefreshCommand", BindingPath(refresh, Button.CommandProperty));
        var addOllama = buttons.Single(b => (string)b.Content! == "Add Ollama Node");
        var addProvider = buttons.Single(b => (string)b.Content! == "Add External Provider");
        Assert.Contains("Primary", addOllama.Classes);
        Assert.Contains("Primary", addProvider.Classes);
    }

    [Fact]
    public void Toolbar_NoAddTargetButton_AndNoOsProviderEntryAnywhere()
    {
        var view = new FleetTargetsView();
        view.ApplyTemplate();
        var (row, _, _) = LoadRowTemplate();

        // The single generic 'Add Target' button must be gone (both trees: the view + the
        // built row template), and there is NO UI path to create an osnative target
        // (native OS inference providers are not implemented — backlog #63/#64).
        Assert.DoesNotContain(
            AllControls(view).Concat(AllControls(row)).OfType<Button>(),
            b => b.Content is string content && content == "Add Target");

        var texts = AllControls(view).Concat(AllControls(row))
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => t is not null)
            .Cast<string>();
        Assert.DoesNotContain(texts, t =>
            t.Contains("osnative", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("OS Provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RowTemplate_IdentityRow_EnabledCluster_IsOneInteractiveCheckBox()
    {
        var (row, identity, _) = LoadRowTemplate();

        // Task #161: the identity grid's enablement cluster (Grid.Column 2) is ONE interactive
        // CheckBox with Content "Enabled" (clicking the box or its label toggles) — the
        // retired label + display-only CheckBox + 'Toggle' button convention.
        var cluster = Assert.IsType<CheckBox>(
            identity.Children.Single(c => Grid.GetColumn(c) == 2));
        Assert.Equal("Enabled", cluster.Content as string);
        Assert.True(cluster.IsHitTestVisible,
            "the enabled CheckBox must be interactive (IsHitTestVisible=true)");

        // The state is a OneWay binding to the row's Enabled; the click runs the UNCHANGED
        // code-behind OnTogglePersistedClick handler (e.Source IS the CheckBox — verified
        // for Avalonia 12's Click pipeline) with the row as Tag.
        Assert.Equal("Enabled", BindingPath(cluster, CheckBox.IsCheckedProperty));
        // The Tag is a bare {Binding} — Avalonia 12 exposes the path-less CompiledBinding's
        // Description as the EMPTY string (not "DataContext").
        Assert.Equal(string.Empty, BindingPath(cluster, Control.TagProperty));
        Assert.Equal(
            "Toggle the persisted (config) enablement flag",
            cluster.GetValue(ToolTip.TipProperty));

        // The retired pieces are gone from the whole row: no "Toggle" button and no
        // standalone "Enabled" TextBlock (the label is the CheckBox's Content). The CheckBox's
        // internal template Button is filtered by Content (layout-test robustness precedent).
        Assert.DoesNotContain(
            AllControls(row).OfType<Button>(),
            b => b.Content is string content && content == "Toggle");
        Assert.DoesNotContain(AllControls(row).OfType<TextBlock>(), tb => tb.Text == "Enabled");
    }

    [Fact]
    public void RowTemplate_EndpointRow_OwnCellPerElement()
    {
        var (_, _, endpoint) = LoadRowTemplate();

        // Task #153: the row has FIVE cells (auto, auto, auto, *, auto) — the 'key: …'
        // cell is gone; the ssh label moved to its own col 4.

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

        // The ssh credential label (col 4) — in its OWN cell. The removed 'key: …' cell must
        // not resurface anywhere in the endpoint row.
        var sshLabel = Assert.IsType<TextBlock>(endpoint.Children.Single(c => Grid.GetColumn(c) == 4));
        Assert.Equal("SshLabel", BindingPath(sshLabel, TextBlock.TextProperty));
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, sshLabel.TextTrimming);
        Assert.Equal("SshLabel", BindingPath(sshLabel, ToolTip.TipProperty));
        Assert.DoesNotContain(
            endpoint.Children.OfType<TextBlock>(),
            tb => BindingPath(tb, TextBlock.TextProperty) == "ApiKeyConfigured");
    }

    [Fact]
    public void RowTemplate_HeaderGrids_NoSharedColumns_SingleStarColumn()
    {
        var (_, identity, endpoint) = LoadRowTemplate();

        // Overlap guard (EditTargetDialogsLayoutTests form-grid no-shared-cell precedent):
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
    public void RowTemplate_HardwareProfile_AlwaysVisible_NoExpander_OneLinePerTopic()
    {
        var (row, _, _) = LoadRowTemplate();

        // Task #148: the profile is ALWAYS EXPANDED — no collapsing container anywhere in the row.
        Assert.Empty(AllControls(row).OfType<Expander>());

        // The section header TextBlock binds the HEADER PROJECTION (task #153; the source is
        // coalesced to "none" there, not in the XAML) and is a direct row-section child.
        var header = AllControls(row).OfType<TextBlock>().Single(tb =>
            BindingPath(tb, TextBlock.TextProperty) == "HardwareProfileHeader");
        // ...and the section that owns it is a direct child of the row StackPanel.
        var section = Assert.IsType<StackPanel>(header.Parent);
        Assert.Contains(section, row.Children);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, header.FontWeight);

        // Task #153, decision 3: the profile-anchored sub-panel carries EXACTLY the three
        // one-line-per-topic projections (OS / memory / GPU), each hidden individually by
        // null text. The old per-field lines, the 'reachable: …' line and the GPU
        // ItemsControl are gone.
        var allText = AllControls(row).OfType<TextBlock>();
        foreach (var expected in new[] { "HardwareOsLine", "HardwareMemoryLine", "HardwareGpuLine" })
        {
            // Single: asserts the line exists exactly once in the card.
            var line = allText.Single(tb => BindingPath(tb, TextBlock.TextProperty) == expected);
            Assert.NotNull(line);
        }
        Assert.DoesNotContain(allText, tb =>
            BindingPath(tb, TextBlock.TextProperty) is "HardwareSource" or "HardwareReachable");
        Assert.Empty(AllControls(row).OfType<ItemsControl>());
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
    public void RowTemplate_EffectiveValues_WrapPanel_ExactlyTheFiveKeptChips()
    {
        var (row, _, _) = LoadRowTemplate();

        // Task #153, decision 6: the effective-values strip is a WrapPanel with EXACTLY five
        // chips — MaxParallel (always), HardwareHint / PowerWatts / KeepAliveLabel /
        // VramOverrideLabel (each null → empty text when unset). The SpeedFactor / MaxVramMB /
        // KeepAliveSource / Resident chips are gone (decisions 1–2: the VRAM and keep-alive
        // values moved to the one-line-per-topic hardware summary / merged label).
        var wrap = Assert.IsType<WrapPanel>(
            row.Children.OfType<WrapPanel>().Single());
        var chips = wrap.Children.OfType<TextBlock>().ToArray();

        var chipPaths = chips.Select(tb => BindingPath(tb, TextBlock.TextProperty)).ToArray();
        Assert.Equal(
            new[] { "MaxParallel", "HardwareHint", "PowerWatts", "KeepAliveLabel", "VramOverrideLabel" },
            chipPaths);
    }

    [Fact]
    public void RowTemplate_Card_RemovedCrypticFieldsAppearNowhere()
    {
        // Task #153 (decisions 1–2): the cryptic/empty chips and the deduplicated VRAM
        // surface must appear NOWHERE in the row template — neither as a binding path nor as
        // literal text (the 'Declared models: N' row, 'key: …', 'reachable: …', the
        // SpeedFactor / Resident / MaxVramMB chips).
        var (row, _, _) = LoadRowTemplate();
        var all = AllControls(row);

        var removedPaths = new[]
        {
            "SpeedFactor", "MaxVramMB", "EffectiveKeepAliveSeconds", "KeepAliveSource",
            "ResidentModels.Count", "ModelsCount", "ApiKeyConfigured", "HardwareReachable",
            "HardwareOs", "HardwareArchitecture", "HardwareSystemRamMB",
            "HardwareTotalFastMemoryMB", "HardwareEffectiveBandwidthGBs",
        };
        foreach (var control in all.OfType<TextBlock>())
        {
            var path = BindingPath(control, TextBlock.TextProperty);
            Assert.False(removedPaths.Contains(path),
                $"a removed display leaf '{path}' still binds a card TextBlock");
        }
        // The GPU list must not resurface as an ItemsControl source (one GPU line instead).
        Assert.DoesNotContain(all.OfType<ItemsControl>(), ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == "Gpus");

        // Literal text (both the template tree and the view tree, the 'Persist'-nowhere
        // precedent): none of the removed caption strings may render anywhere on the card.
        var view = new FleetTargetsView();
        view.ApplyTemplate();
        var texts = AllControls(view).Concat(all).OfType<TextBlock>()
            .Select(tb => tb.Text)
            .Where(tb => tb is not null)
            .Cast<string>();
        foreach (var removed in new[]
        {
            "Declared models", "key:", "reachable:", "SpeedFactor:", "Resident:", "MaxVramMB:",
        })
        {
            Assert.True(
                texts.All(tb => !tb.Contains(removed)),
                $"the literal '{removed}' must not appear anywhere on the card");
        }
    }

    /// <summary>
    /// The tab's toolbar <see cref="StackPanel"/>: <c>view.Content</c> →
    /// <see cref="ScrollViewer"/> → root <see cref="StackPanel"/> → child 1 (child 0 is the
    /// last-error banner TextBlock).
    /// </summary>
    private static StackPanel GetToolbar()
    {
        var view = new FleetTargetsView();
        HeadlessThemeHelper.Apply(view);
        view.ApplyTemplate();
        var scrollViewer = Assert.IsType<ScrollViewer>(view.Content);
        var root = Assert.IsType<StackPanel>(scrollViewer.Content);
        return Assert.IsType<StackPanel>(root.Children[1]);
    }

    private static (StackPanel Row, Grid Identity, Grid Endpoint) LoadRowTemplate()
    {
        var view = new FleetTargetsView();
        HeadlessThemeHelper.Apply(view);
        view.ApplyTemplate();
        var all = AllControls(view).ToList();

        var itemsControl = all.OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == "Rows");
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        var built = template.Build(null);

        // The unattached tree does not resolve data bindings headlessly (no DataContext /
        // tree attach), so the row assertions are binding-PATH assertions; the exact rendered
        // strings of the populated test-session row are owned by the
        // FleetTargetsViewModelTests projection tests and FleetTargetDisplayTests.
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
