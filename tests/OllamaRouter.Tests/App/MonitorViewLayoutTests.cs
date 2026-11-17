using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Monitor view layout contract tests (task #132): pins the inter-column separation of the four
/// fixed-width tables — every header grid and its row-template grid carries
/// <see cref="Grid.ColumnSpacing"/> of 8 (so the right-aligned IN-FLIGHT / CONTEXT / ELAPSED
/// values no longer butt against the next column's left-aligned content), the header and row
/// grids declare byte-identical <see cref="Grid.ColumnDefinitions"/>, and the net-neutral width
/// strategy (Strategy A, maintainer-confirmed) keeps each table's rendered total
/// (Σ fixed pixel widths + (n−1)×8) at or below its pre-fix total. Also pins the ENABLED
/// column's display-only <see cref="CheckBox"/> + <c>Toggle</c> button contract, which the
/// grid-level spacing must not have disturbed.
/// </summary>
/// <remarks>
/// [Collection("Avalonia")]: this class creates <see cref="MonitorView"/> control trees, so it
/// must never run in parallel with the other Avalonia-control-creating test classes
/// (AgentSessionsViewLayoutTests, PreloadDialogLayoutTests, ProportionalPanelTests) —
/// Avalonia 12 keeps process-wide mutable state (the thread-unsafe
/// AvaloniaPropertyDictionaryPool static stack and the first-touch-owned Dispatcher.UIThread),
/// which corrupts under concurrent control construction.
/// </remarks>
[Collection("Avalonia")]
public sealed class MonitorViewLayoutTests
{
    private static readonly int[] PreFixWidthBaselines = [1060, 890, 760, 930];
    private static readonly string[] BaselineTableNames =
        ["fleet table", "in-progress requests table", "stats table", "recent requests table"];

    [Fact]
    public void FleetTable_HeaderAndRow_CarryColumnSpacingOf8()
    {
        var tables = LoadTables();

        Assert.True(tables.FleetHeader.ColumnSpacing == 8.0,
            "fleet header grid has ColumnSpacing " + tables.FleetHeader.ColumnSpacing + ", expected 8");
        Assert.True(tables.FleetRow.ColumnSpacing == 8.0,
            "fleet row grid has ColumnSpacing " + tables.FleetRow.ColumnSpacing + ", expected 8");
    }

    [Fact]
    public void FleetTable_HeaderAndRowGrids_DeclareIdenticalColumnWidths()
    {
        var tables = LoadTables();

        AssertSameColumnWidths("fleet table", tables.FleetHeader, tables.FleetRow);
    }

    [Fact]
    public void FleetTable_InFlightAndContextRightAlignedInHeaderAndValues_EnabledLeftAligned()
    {
        var tables = LoadTables();
        var inFlightColumn = HeaderColumn(tables.FleetHeader, "IN-FLIGHT");
        var contextColumn = HeaderColumn(tables.FleetHeader, "CONTEXT");
        var enabledColumn = HeaderColumn(tables.FleetHeader, "ENABLED");

        AssertRightAlignedPair("IN-FLIGHT", inFlightColumn,
            tables.FleetHeader, tables.FleetRow, "InFlightRequests");
        AssertRightAlignedPair("CONTEXT", contextColumn,
            tables.FleetHeader, tables.FleetRow, "ContextAllocation");
        Assert.True(tables.FleetHeader.Children.OfType<TextBlock>()
                    .Single(tb => tb.Text == "ENABLED").TextAlignment == Avalonia.Media.TextAlignment.Start,
            "ENABLED header is not left-aligned");
    }

    [Fact]
    public void FleetRow_EnabledColumn_ContainsDisplayOnlyCheckBoxAndToggleButton()
    {
        var tables = LoadTables();
        var enabledColumn = HeaderColumn(tables.FleetHeader, "ENABLED");

        var cell = tables.FleetRow.Children.Single(c => Grid.GetColumn(c) == enabledColumn);

        // The ENABLED cell is a horizontal StackPanel holding the display-only indicator and the
        // interactive Toggle button (inner Spacing=8, button Padding=8,2 — both preserved).
        var panel = Assert.IsType<StackPanel>(cell);
        var checkBox = AllControls(cell).OfType<CheckBox>().Single();
        Assert.False(checkBox.IsHitTestVisible, "ENABLED CheckBox must stay display-only (IsHitTestVisible=false)");

        // CheckBox derives from ButtonBase (not Button) in Avalonia 12; the defensive filter
        // keeps this robust if the hierarchy ever changes.
        var button = AllControls(cell).OfType<Button>().Single(b => b is not CheckBox);
        Assert.Equal("Toggle", button.Content as string);
        // The command is a binding (ToggleCommand), evaluated only against a DataContext — assert
        // the binding is attached rather than the (pre-databinding) resolved value.
        var commandBinding = Avalonia.Data.BindingOperations.GetBindingExpressionBase(button, Button.CommandProperty);
        Assert.True(commandBinding is not null, "Toggle button lost its Command binding");
        Assert.Equal("ToggleCommand", BindingPath(button, Button.CommandProperty));
    }

    [Fact]
    public void InProgressTable_HeaderAndRow_CarryColumnSpacingOf8()
    {
        var tables = LoadTables();

        Assert.True(tables.InProgressHeader.ColumnSpacing == 8.0,
            "in-progress header grid has ColumnSpacing " + tables.InProgressHeader.ColumnSpacing + ", expected 8");
        Assert.True(tables.InProgressRow.ColumnSpacing == 8.0,
            "in-progress row grid has ColumnSpacing " + tables.InProgressRow.ColumnSpacing + ", expected 8");
    }

    [Fact]
    public void InProgressTable_ElapsedRightAligned_ReasoningLeftAligned()
    {
        var tables = LoadTables();
        var elapsedColumn = HeaderColumn(tables.InProgressHeader, "ELAPSED");
        var reasoningColumn = HeaderColumn(tables.InProgressHeader, "REASONING");

        AssertRightAlignedPair("ELAPSED", elapsedColumn,
            tables.InProgressHeader, tables.InProgressRow, "ElapsedMs");
        Assert.True(tables.InProgressHeader.Children.OfType<TextBlock>()
                    .Single(tb => tb.Text == "REASONING").TextAlignment == Avalonia.Media.TextAlignment.Start,
            "REASONING header is not left-aligned");
        var reasoningValue = tables.InProgressRow.Children.OfType<TextBlock>()
            .Single(tb => Grid.GetColumn(tb) == reasoningColumn);
        Assert.True(reasoningValue.TextAlignment == Avalonia.Media.TextAlignment.Start,
            "REASONING row value is not left-aligned");
    }

    [Fact]
    public void InProgressTable_HeaderAndRowGrids_DeclareIdenticalColumnWidths()
    {
        var tables = LoadTables();

        AssertSameColumnWidths("in-progress requests table", tables.InProgressHeader, tables.InProgressRow);
    }

    [Fact]
    public void StatsTable_CarryColumnSpacingOf8()
    {
        var tables = LoadTables();

        Assert.True(tables.StatsHeader.ColumnSpacing == 8.0,
            "stats header grid has ColumnSpacing " + tables.StatsHeader.ColumnSpacing + ", expected 8");
        Assert.True(tables.StatsRow.ColumnSpacing == 8.0,
            "stats row grid has ColumnSpacing " + tables.StatsRow.ColumnSpacing + ", expected 8");
    }

    [Fact]
    public void RecentRequestsTable_CarryColumnSpacingOf8()
    {
        var tables = LoadTables();

        Assert.True(tables.RecentHeader.ColumnSpacing == 8.0,
            "recent-requests header grid has ColumnSpacing " + tables.RecentHeader.ColumnSpacing + ", expected 8");
        Assert.True(tables.RecentRow.ColumnSpacing == 8.0,
            "recent-requests row grid has ColumnSpacing " + tables.RecentRow.ColumnSpacing + ", expected 8");
    }

    [Fact]
    public void AllTables_TotalRenderedWidths_DoNotGrowVsBaseline()
    {
        var tables = LoadTables();

        // Pre-fix totals (Σ fixed pixel widths, no inter-column gaps): fleet 1060, in-progress
        // fixed 890 (star column contributes 0), stats 760, recent 930. Strategy A
        // (task #132, maintainer-confirmed): Σ fixed + (n-1)×8 must not exceed the pre-fix total,
        // so the min-window horizontal-scroll range does not grow.
        var grids = new[]
        {
            tables.FleetHeader, tables.InProgressHeader, tables.StatsHeader, tables.RecentHeader,
        };
        for (var i = 0; i < grids.Length; i++)
        {
            var total = TotalRenderedWidth(grids[i]);
            Assert.True(total <= PreFixWidthBaselines[i],
                $"{BaselineTableNames[i]} renders {total}px wide, pre-fix baseline was {PreFixWidthBaselines[i]}px");
        }
    }

    // ── Shared helpers ──

    private static (MonitorView View, Grid FleetHeader, Grid FleetRow,
                    Grid InProgressHeader, Grid InProgressRow,
                    Grid StatsHeader, Grid StatsRow,
                    Grid RecentHeader, Grid RecentRow) LoadTables()
    {
        var view = new MonitorView();
        view.ApplyTemplate();
        var all = AllControls(view).ToList();

        var fleetHeader = all.OfType<Grid>().Single(g =>
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "IN-FLIGHT") &&
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "ENABLED") &&
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "LIVENESS"));
        var inProgressHeader = all.OfType<Grid>().Single(g =>
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "EST. TOKENS") &&
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "REASONING"));
        var statsHeader = all.OfType<Grid>().Single(g =>
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "TODAY REQ") &&
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "7D GPU TIME"));
        var recentHeader = all.OfType<Grid>().Single(g =>
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "TIMESTAMP") &&
            g.Children.OfType<TextBlock>().Any(tb => tb.Text == "STATUS"));

        return (view,
            fleetHeader, BuildRowGrid(all, "FleetRows"),
            inProgressHeader, BuildRowGrid(all, "ActiveRequests"),
            statsHeader, BuildRowGrid(all, "StatisticsRows"),
            recentHeader, BuildRowGrid(all, "RecentRequests"));
    }

    /// <summary>
    /// Builds the row-template grid of the <see cref="ItemsControl"/> whose <c>ItemsSource</c>
    /// binding path is <paramref name="expectedItemsSourcePath"/>. The stats template root is a
    /// <see cref="Border"/>, so its inner <see cref="Grid"/> is reached through
    /// <see cref="Decorator.Child"/>.
    /// </summary>
    private static Grid BuildRowGrid(IReadOnlyList<Control> all, string expectedItemsSourcePath)
    {
        var itemsControl = all.OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == expectedItemsSourcePath);
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        var built = template.Build(null);
        return built switch
        {
            Grid grid => grid,
            Decorator { Child: Grid innerGrid } => innerGrid,
            _ => throw new InvalidOperationException(
                $"row template for [{expectedItemsSourcePath}] does not contain a Grid at its root (got {built?.GetType().Name})"),
        };
    }

    private static int HeaderColumn(Grid header, string marker)
    {
        var markerBlock = header.Children.OfType<TextBlock>().Single(tb => tb.Text == marker);
        return Grid.GetColumn(markerBlock);
    }

    private static void AssertRightAlignedPair(string label, int column, Grid header, Grid row, string valueBindingPath)
    {
        var headerBlock = header.Children.OfType<TextBlock>().Single(tb => Grid.GetColumn(tb) == column);
        Assert.True(headerBlock.TextAlignment == Avalonia.Media.TextAlignment.Right,
            $"{label} header is {headerBlock.TextAlignment}, expected Right");
        var valueBlock = row.Children.OfType<TextBlock>().Single(tb => Grid.GetColumn(tb) == column);
        Assert.True(valueBlock.TextAlignment == Avalonia.Media.TextAlignment.Right,
            $"{label} row value is {valueBlock.TextAlignment}, expected Right");
        var actualPath = BindingPath(valueBlock, TextBlock.TextProperty);
        Assert.True(actualPath == valueBindingPath,
            $"{label} row value binds to [{actualPath}], expected [{valueBindingPath}]");
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

    private static int TotalRenderedWidth(Grid grid)
    {
        var fixedSum = grid.ColumnDefinitions
            .Where(c => c.Width.IsAbsolute)
            .Sum(c => (int)c.Width.Value);
        return fixedSum + (grid.ColumnDefinitions.Count - 1) * (int)grid.ColumnSpacing;
    }

    /// <summary>
    /// Returns the XAML path of the binding on <paramref name="property"/> (e.g.
    /// <c>FleetRows</c>), or null when the property carries no binding.
    /// Avalonia 12's concrete <c>BindingExpression</c> type is internal, so the
    /// human-readable <c>Description</c> is read reflectively.
    /// </summary>
    private static string? BindingPath(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Description")?.GetValue(expression) as string;
    }

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
