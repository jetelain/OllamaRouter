using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Agents &amp; Sessions view layout contract tests (task #129): pins the CLOUD VALUE rename
/// (no "EST. COST" header anywhere; exactly four "CLOUD VALUE" labels), the right-alignment of
/// every numeric column (header AND values, matched per <see cref="Grid.GetColumn"/>), the
/// byte-identical header/row <see cref="Grid.ColumnDefinitions"/>, and the scoped
/// <c>ListBox &gt; ListBoxItem</c> padding-zero style that neutralizes the Semi theme's 12px
/// ListBoxItem padding (the root cause of the header/value offset).
/// </summary>
/// <remarks>
/// [Collection("Avalonia")] (task #129): this class creates <see cref="AgentSessionsView"/>
/// control trees, so it must never run in parallel with the other Avalonia-control-creating test
/// classes (PreloadDialogLayoutTests, ProportionalPanelTests) — Avalonia 12 keeps process-wide
/// mutable state for that (the thread-unsafe AvaloniaPropertyDictionaryPool static stack and the
/// first-touch-owned Dispatcher.UIThread), which corrupts under concurrent control construction.
/// </remarks>
[Collection("Avalonia")]
public sealed class AgentSessionsViewLayoutTests
{
    private static readonly int[] MainNumericColumns = [1, 2, 3, 4, 5, 7, 8];
    private static readonly int[] MainTextColumns = [0, 6]; // AGENT / SESSION ID, LAST SEEN
    private static readonly int[] DetailNumericColumns = [0, 1, 2, 3, 4, 6, 7, 8];
    private static readonly int[] DetailTextColumns = [5]; // LAST SEEN
    private static readonly int[] TargetNumericColumns = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly int[] TargetTextColumns = [0]; // TARGET

    // ── AC1: EST. COST → CLOUD VALUE rename (data bindings untouched) ──

    [Fact]
    public void View_ContainsNoEstCostLabel()
    {
        var texts = AllControls(LoadView())
            .OfType<TextBlock>()
            .Select(tb => tb.Text)
            .Where(t => t is not null)
            .ToList();

        Assert.Empty(texts.Where(t => t!.StartsWith("EST. COST", StringComparison.Ordinal)));
    }

    [Fact]
    public void View_ContainsExactlyFourCloudValueLabels()
    {
        // Three table headers (main sessions, session detail, per-target) + one summary card label.
        var count = AllControls(LoadView())
            .OfType<TextBlock>()
            .Count(tb => tb.Text == "CLOUD VALUE");

        Assert.Equal(4, count);
    }

    [Fact]
    public void CloudValueCells_StillBindToFormattedCloudValue()
    {
        var tables = LoadTables();

        // Main sessions row, session detail, and per-target breakdown all keep the cloud-value
        // binding; only the header text changed.
        AssertTextBindingPath(tables.MainRow, 7, "FormattedCloudValue");
        AssertTextBindingPath(tables.DetailRow, 6, "SelectedRow.FormattedCloudValue");
        AssertTextBindingPath(tables.TargetRow, 6, "FormattedCloudValue");

        // Summary card still feeds the aggregate cloud value under the renamed label.
        var summaryValue = AllControls(tables.View).OfType<TextBlock>().SingleOrDefault(tb =>
            BindingPath(tb, TextBlock.TextProperty) == "Summary.FormattedTotalCloudValue");
        Assert.True(summaryValue is not null,
            "summary card value no longer binds to Summary.FormattedTotalCloudValue");
    }

    // ── AC2: numeric columns right-aligned (header and values); text columns stay left ──

    [Fact]
    public void AllTables_NumericColumns_AreRightAlignedInHeaderAndValues()
    {
        var tables = LoadTables();

        AssertColumnsAligned("main sessions table", tables.MainHeader, tables.MainRow,
            MainNumericColumns, MainTextColumns);
        AssertColumnsAligned("session detail table", tables.DetailHeader, tables.DetailRow,
            DetailNumericColumns, DetailTextColumns);
        AssertColumnsAligned("per-target breakdown table", tables.TargetHeader, tables.TargetRow,
            TargetNumericColumns, TargetTextColumns);
    }

    // ── AC3: header and row grids declare identical column widths ──

    [Fact]
    public void AllTables_HeaderAndRowGrids_DeclareIdenticalColumnWidths()
    {
        var tables = LoadTables();

        AssertSameColumnWidths("main sessions table", tables.MainHeader, tables.MainRow);
        AssertSameColumnWidths("session detail table", tables.DetailHeader, tables.DetailRow);
        AssertSameColumnWidths("per-target breakdown table", tables.TargetHeader, tables.TargetRow);
    }

    // ── AC: ColumnSpacing=8 on every table grid; header/row parity; net-neutral totals (task #132) ──

    [Fact]
    public void AllTables_CarryColumnSpacingOf8()
    {
        var tables = LoadTables();

        foreach (var grid in AllTableGrids(tables))
        {
            Assert.True(grid.ColumnSpacing == 8.0,
                $"grid [{string.Join(",", grid.ColumnDefinitions.Select(c => c.Width.ToString()))}] " +
                $"has ColumnSpacing {grid.ColumnSpacing}, expected 8");
        }
    }

    [Fact]
    public void AllTables_HeaderAndRowGrids_DeclareIdenticalColumnSpacing()
    {
        var tables = LoadTables();

        AssertSpacingParity("main sessions table", tables.MainHeader, tables.MainRow);
        AssertSpacingParity("session detail table", tables.DetailHeader, tables.DetailRow);
        AssertSpacingParity("per-target breakdown table", tables.TargetHeader, tables.TargetRow);
    }

    [Fact]
    public void AllTables_TotalRenderedWidths_DoNotGrowVsBaseline()
    {
        var tables = LoadTables();

        // Pre-fix totals (Σ fixed pixel widths, no inter-column gaps): main 1010, detail 940,
        // per-target 910. Strategy A (task #132, maintainer-confirmed): Σ fixed + (n-1)×8 must not
        // exceed the pre-fix total, so the min-window horizontal-scroll range does not grow.
        Assert.True(TotalRenderedWidth(tables.MainHeader) <= 1010,
            $"main sessions table renders {TotalRenderedWidth(tables.MainHeader)}px wide, pre-fix baseline was 1010px");
        Assert.True(TotalRenderedWidth(tables.DetailHeader) <= 940,
            $"session detail table renders {TotalRenderedWidth(tables.DetailHeader)}px wide, pre-fix baseline was 940px");
        Assert.True(TotalRenderedWidth(tables.TargetHeader) <= 910,
            $"per-target breakdown table renders {TotalRenderedWidth(tables.TargetHeader)}px wide, pre-fix baseline was 910px");
    }

    // ── AC4: ListBoxItem padding offset neutralized, no compensating margins ──

    [Fact]
    public void SessionsListBox_ZeroesListBoxItemPaddingViaScopedStyle()
    {
        var view = LoadView();
        var listBox = AllControls(view).OfType<ListBox>().Single();

        var style = listBox.Styles.OfType<Style>().Single(s =>
            s.Selector?.ToString() == "ListBox > ListBoxItem");

        var setter = style.Setters.OfType<Setter>().Single();
        Assert.Equal("Padding", setter.Property.Name);
        Assert.Equal(new Thickness(0), (Thickness)setter.Value);
    }

    [Fact]
    public void AllTables_HeaderAndRowGrids_HaveNoHorizontalMargin()
    {
        var tables = LoadTables();

        foreach (var grid in new[]
                 {
                     tables.MainHeader, tables.MainRow,
                     tables.DetailHeader, tables.DetailRow,
                     tables.TargetHeader, tables.TargetRow,
                 })
        {
            Assert.True(grid.Margin.Left == 0 && grid.Margin.Right == 0,
                $"grid with ColumnDefinitions [{string.Join(",", grid.ColumnDefinitions.Select(c => c.Width.ToString()))}] " +
                $"has a horizontal margin ({grid.Margin.Left}, {grid.Margin.Right}) shifting it away from the other grid");
        }
    }

    // ── AC5: regression guards for the untouched parts of the view ──

    [Fact]
    public void EmptyStateHint_IsStillPresent()
    {
        var hint = AllControls(LoadView())
            .OfType<TextBlock>()
            .FirstOrDefault(tb => (tb.Text ?? string.Empty).StartsWith("No agent sessions recorded yet", StringComparison.Ordinal));

        Assert.True(hint is not null, "empty-state hint TextBlock is missing from the view");
    }

    [Fact]
    public void AgentIdCell_StillTrimsWithEllipsisAndTooltip()
    {
        var tables = LoadTables();
        var agentCell = tables.MainRow.Children
            .OfType<TextBlock>()
            .Single(tb => Grid.GetColumn(tb) == 0);

        Assert.Equal(TextTrimming.CharacterEllipsis, agentCell.TextTrimming);
        Assert.True(BindingOperations.GetBindingExpressionBase(agentCell, ToolTip.TipProperty) is not null,
            "agent ID cell lost its ToolTip.Tip binding");
    }

    // ── Shared helpers ──

    private static void AssertColumnsAligned(string tableName, Grid header, Grid row, int[] numericColumns, int[] textColumns)
    {
        Assert.Equal(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);

        for (var col = 0; col < header.ColumnDefinitions.Count; col++)
        {
            var headerBlock = header.Children.OfType<TextBlock>().Single(tb => Grid.GetColumn(tb) == col);

            if (numericColumns.Contains(col))
            {
                var valueBlock = row.Children.OfType<TextBlock>().Single(tb => Grid.GetColumn(tb) == col);
                Assert.True(headerBlock.TextAlignment == TextAlignment.Right,
                    $"{tableName}: column {col} header is {headerBlock.TextAlignment}, expected Right");
                Assert.True(valueBlock.TextAlignment == TextAlignment.Right,
                    $"{tableName}: column {col} value is {valueBlock.TextAlignment}, expected Right");
            }
            else if (textColumns.Contains(col))
            {
                Assert.True(headerBlock.TextAlignment == TextAlignment.Start,
                    $"{tableName}: text column {col} header is {headerBlock.TextAlignment}, expected Start (left)");
                var valueBlock = row.Children.OfType<TextBlock>().FirstOrDefault(tb => Grid.GetColumn(tb) == col);
                if (valueBlock is not null) // per-target col 0 is a StackPanel, not a TextBlock
                    Assert.True(valueBlock.TextAlignment == TextAlignment.Start,
                        $"{tableName}: text column {col} value is {valueBlock.TextAlignment}, expected Start (left)");
            }
        }
    }

    private static void AssertSpacingParity(string tableName, Grid header, Grid row)
    {
        Assert.True(header.ColumnSpacing == row.ColumnSpacing,
            $"{tableName}: header grid declares ColumnSpacing {header.ColumnSpacing} " +
            $"but the row grid declares {row.ColumnSpacing}");
    }

    private static IEnumerable<Grid> AllTableGrids(
        (AgentSessionsView View, Grid MainHeader, Grid MainRow,
         Grid DetailHeader, Grid DetailRow, Grid TargetHeader, Grid TargetRow) tables)
    {
        yield return tables.MainHeader;
        yield return tables.MainRow;
        yield return tables.DetailHeader;
        yield return tables.DetailRow;
        yield return tables.TargetHeader;
        yield return tables.TargetRow;
    }

    /// <summary>
    /// Total rendered width of a table grid: Σ fixed pixel column widths + (n-1)×ColumnSpacing
    /// (star columns contribute no fixed width).
    /// </summary>
    private static int TotalRenderedWidth(Grid grid)
    {
        var fixedSum = grid.ColumnDefinitions
            .Where(c => c.Width.IsAbsolute)
            .Sum(c => (int)c.Width.Value);
        return fixedSum + (grid.ColumnDefinitions.Count - 1) * (int)grid.ColumnSpacing;
    }

    private static void AssertSameColumnWidths(string tableName, Grid header, Grid row)
    {
        Assert.Equal(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);
        for (var i = 0; i < header.ColumnDefinitions.Count; i++)
        {
            Assert.True(header.ColumnDefinitions[i].Width.Equals(row.ColumnDefinitions[i].Width),
                $"{tableName}: column {i} width is {header.ColumnDefinitions[i].Width} in the header grid " +
                $"but {row.ColumnDefinitions[i].Width} in the row grid");
        }
    }

    private static void AssertTextBindingPath(Grid row, int column, string expectedPath)
    {
        var block = row.Children.OfType<TextBlock>().Single(tb => Grid.GetColumn(tb) == column);
        var actual = BindingPath(block, TextBlock.TextProperty);
        Assert.True(actual == expectedPath,
            $"cloud-value cell in {cellDescription(row)} col {column} binds to [{actual}], expected [{expectedPath}]");
    }

    private static string cellDescription(Grid row) =>
        $"grid [{string.Join(",", row.ColumnDefinitions.Select(c => c.Width.ToString()))}]";

    /// <summary>
    /// Returns the XAML path of the binding on <paramref name="property"/> (e.g.
    /// <c>FormattedCloudValue</c>), or null when the property carries no binding.
    /// Avalonia 12's concrete <c>BindingExpression</c> type is internal, so the
    /// human-readable <c>Description</c> is read reflectively.
    /// </summary>
    private static string? BindingPath(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Description")?.GetValue(expression) as string;
    }

    private static AgentSessionsView LoadView()
    {
        // Avalonia 12 source-generates the XAML: the full content tree is built in the
        // constructor — no Application / headless platform needed (and HeadlessUnitTestSession
        // is deliberately avoided: it deadlocks in this environment).
        var view = new AgentSessionsView();
        view.ApplyTemplate();
        return view;
    }

    private static (AgentSessionsView View, Grid MainHeader, Grid MainRow,
                    Grid DetailHeader, Grid DetailRow, Grid TargetHeader, Grid TargetRow) LoadTables()
    {
        var view = LoadView();
        var all = AllControls(view).ToList();

        var mainHeader = HeaderGrid(all, "AGENT / SESSION ID");
        var detailHeader = all.OfType<Grid>().Single(g =>
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "REQUESTS") &&
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "SAVINGS"));
        var targetHeader = HeaderGrid(all, "TARGET");

        var listBox = all.OfType<ListBox>().Single();
        var targetItemsControl = all.OfType<ItemsControl>().Single(ic => ic is not ListBox);

        var mainRow = BuildItemGrid(listBox);
        var targetRow = BuildItemGrid(targetItemsControl);

        // The session-detail value grid is the second grid sharing the detail header's exact
        // ColumnDefinitions (its TextBlocks are all binding-driven, so Text is null pre-databinding).
        var detailRow = all.OfType<Grid>().Single(g =>
            !ReferenceEquals(g, detailHeader) &&
            HasSameColumnWidths(g, detailHeader) &&
            g.Children.OfType<TextBlock>().All(tb => string.IsNullOrEmpty(tb.Text)));

        return (view, mainHeader, mainRow, detailHeader, detailRow, targetHeader, targetRow);
    }

    private static Grid HeaderGrid(IReadOnlyList<Control> all, string marker) =>
        all.OfType<Grid>().Single(g => g.Children.OfType<TextBlock>().Any(tb => tb.Text == marker));

    private static Grid BuildItemGrid(ItemsControl itemsControl)
    {
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        var built = template.Build(null);
        return AllControls(built).OfType<Grid>().First();
    }

    private static bool HasSameColumnWidths(Grid grid, Grid other) =>
        grid.ColumnDefinitions.Count == other.ColumnDefinitions.Count &&
        Enumerable.Range(0, grid.ColumnDefinitions.Count)
            .All(i => grid.ColumnDefinitions[i].Width.Equals(other.ColumnDefinitions[i].Width));

    /// <summary>
    /// Walks the <em>content</em> tree (Panel children, Decorator child, ContentControl content),
    /// not the visual tree — visual children only materialize under a themed TopLevel.
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
