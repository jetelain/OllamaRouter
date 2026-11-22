using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Monitor view layout contract tests (task #132, re-baselined by task #145): pins the
/// inter-column separation of the four fixed-width tables — every header grid and its
/// row-template grid carries <see cref="Grid.ColumnSpacing"/> of 8 (so the right-aligned
/// IN-FLIGHT / CONTEXT / ELAPSED values no longer butt against the next column's left-aligned
/// content), the header and row grids declare byte-identical <see cref="Grid.ColumnDefinitions"/>,
/// and every table's rendered total (Σ fixed pixel widths + (n−1)×8) fits the 896px budget of
/// the 960px minimum window (the outer ScrollViewer's horizontal scrollbar is disabled, so
/// nothing may be clipped). Also pins the ENABLED column's single interactive
/// <see cref="CheckBox"/> (task #161 — the retired display-only CheckBox + "Toggle" button
/// contract: the cell IS the CheckBox, OneWay <c>IsChecked</c> on "Enabled" + command-bound),
/// the Fleet TARGET cell's URL tooltip (the URL column was
/// removed), the uniform TARGET (120) / MODEL (150) widths across the In-Progress / Recent /
/// Statistics tables, and the In-Progress star SESSION + fixed-100 AGENT layout.
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
    /// <summary>
    /// Table-grid width budget at the 960px minimum window (task #145, A1):
    /// 960 − 12 (TabControl Margin="6,4,6,6") − 24 (view StackPanel margin 12px each side)
    /// − 28 (card Border padding 14px each side) = 896. Star columns contribute 0 fixed width.
    /// </summary>
    private const int MinWindowTableBudget = 896;

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
    public void FleetRow_EnabledColumn_IsOneInteractiveCheckBox()
    {
        var tables = LoadTables();
        var enabledColumn = HeaderColumn(tables.FleetHeader, "ENABLED");

        // Task #161: the ENABLED cell IS the interactive CheckBox (no wrapper panel) — the
        // retired display-only CheckBox + "Toggle" button convention. The "ENABLED" column
        // header carries the label, so the CheckBox is bare (no Content).
        var cell = tables.FleetRow.Children.Single(c => Grid.GetColumn(c) == enabledColumn);
        var checkBox = Assert.IsType<CheckBox>(cell);
        Assert.True(checkBox.IsHitTestVisible,
            "the ENABLED CheckBox must be interactive (IsHitTestVisible=true)");
        Assert.True(checkBox.Content as string is null, "the Monitor ENABLED CheckBox must be bare (header carries the label)");

        // The state is a OneWay binding to the row's Enabled; the click runs ToggleCommand
        // (the command owns the optimistic flip / revert) with the row as the parameter.
        Assert.Equal("Enabled", BindingPath(checkBox, CheckBox.IsCheckedProperty));
        Assert.True(BindingOperations.GetBindingExpressionBase(checkBox, Button.CommandProperty) is not null,
            "the ENABLED CheckBox lost its Command binding");
        Assert.Equal("ToggleCommand", BindingPath(checkBox, Button.CommandProperty));
        Assert.True(BindingOperations.GetBindingExpressionBase(checkBox, Button.CommandParameterProperty) is not null,
            "the ENABLED CheckBox lost its CommandParameter binding");

        // The separate "Toggle" button is gone (the CheckBox's internal template Button is
        // filtered by Content — layout-test robustness precedent).
        Assert.DoesNotContain(
            AllControls(tables.FleetRow).OfType<Button>(),
            b => b.Content is string content && content == "Toggle");
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
    public void AllTables_TotalRenderedWidths_FitTheMinWindowBudget()
    {
        var tables = LoadTables();

        // Task #145: the outer ScrollViewer's horizontal scrollbar is disabled, so every table
        // must render fully inside the 896px budget of the 960px minimum window (Σ fixed pixel
        // widths + (n−1)×8; star columns contribute 0). Largest table: fleet at 860px.
        var tablesByGrid = new[]
        {
            ("fleet table", tables.FleetHeader),
            ("in-progress requests table", tables.InProgressHeader),
            ("stats table", tables.StatsHeader),
            ("recent requests table", tables.RecentHeader),
        };
        foreach (var (name, grid) in tablesByGrid)
        {
            var total = TotalRenderedWidth(grid);
            Assert.True(total <= MinWindowTableBudget,
                $"{name} renders {total}px wide, but the min-window table budget is {MinWindowTableBudget}px");
        }
    }

    [Fact]
    public void FleetTable_HasEightColumns_AndNoUrlHeader()
    {
        var tables = LoadTables();

        // Task #145: the URL column was removed from the Fleet table (8 columns, down from 9);
        // the URL survives as a hover tooltip on the TARGET cell.
        Assert.Equal(8, tables.FleetHeader.ColumnDefinitions.Count);
        Assert.DoesNotContain(tables.FleetHeader.Children.OfType<TextBlock>(), tb => tb.Text == "URL");
    }

    [Fact]
    public void FleetRow_TargetCell_CarriesUrlTooltip()
    {
        var tables = LoadTables();

        var targetCell = tables.FleetRow.Children.Single(c => Grid.GetColumn(c) == 0);
        var panel = Assert.IsType<StackPanel>(targetCell);
        Assert.True(Avalonia.Data.BindingOperations.GetBindingExpressionBase(panel, ToolTip.TipProperty) is not null,
            "fleet TARGET cell lost its ToolTip.Tip binding");
        Assert.Equal("Url", BindingPath(panel, ToolTip.TipProperty));
    }

    [Fact]
    public void TargetAndModelColumns_AreUniformAcrossMonitorTables()
    {
        var tables = LoadTables();

        // Task #145: one TARGET width and one MODEL width across the three Monitor tables.
        Assert.Equal(120.0, AbsoluteWidth(tables.InProgressHeader, "TARGET"));
        Assert.Equal(120.0, AbsoluteWidth(tables.RecentHeader, "TARGET"));
        Assert.Equal(120.0, AbsoluteWidth(tables.StatsHeader, "TARGET"));
        Assert.Equal(150.0, AbsoluteWidth(tables.InProgressHeader, "MODEL"));
        Assert.Equal(150.0, AbsoluteWidth(tables.RecentHeader, "MODEL"));
    }

    [Fact]
    public void InProgressTable_SessionIsStar_AndAgentIsFixedWidthWithTooltip()
    {
        var tables = LoadTables();
        var sessionColumn = HeaderColumn(tables.InProgressHeader, "SESSION");
        var agentColumn = HeaderColumn(tables.InProgressHeader, "AGENT");

        // Task #145: SESSION absorbs the freed width (star) so long session IDs render in full
        // at 960px; AGENT is fixed-width with ellipsis + tooltip.
        Assert.True(tables.InProgressHeader.ColumnDefinitions[sessionColumn].Width.IsStar,
            $"SESSION column is {tables.InProgressHeader.ColumnDefinitions[sessionColumn].Width}, expected star");
        var agentWidth = tables.InProgressHeader.ColumnDefinitions[agentColumn].Width;
        Assert.True(agentWidth.IsAbsolute && agentWidth.Value == 100.0,
            $"AGENT column is {agentWidth}, expected absolute 100");

        var agentCell = tables.InProgressRow.Children.OfType<TextBlock>().Single(tb => Grid.GetColumn(tb) == agentColumn);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, agentCell.TextTrimming);
        Assert.True(Avalonia.Data.BindingOperations.GetBindingExpressionBase(agentCell, ToolTip.TipProperty) is not null,
            "AGENT cell lost its ToolTip.Tip binding");
        Assert.Equal("AgentId", BindingPath(agentCell, ToolTip.TipProperty));
    }

    [Fact]
    public void RecentRequestsTable_TimestampColumn_Is76px()
    {
        var tables = LoadTables();

        // Task #145: TIMESTAMP shrank from 124px to just enough for the HH:mm:ss time-only format.
        Assert.Equal(76.0, AbsoluteWidth(tables.RecentHeader, "TIMESTAMP"));
    }

    [Fact]
    public void OuterScrollViewer_DisablesHorizontalScroll()
    {
        var view = new MonitorView();
        view.ApplyTemplate();

        // Task #145: no page-level horizontal scroll — everything must fit the 960px minimum window.
        var scrollView = AllControls(view).OfType<ScrollViewer>().First();
        Assert.Equal(ScrollBarVisibility.Disabled, scrollView.HorizontalScrollBarVisibility);
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

    private static double AbsoluteWidth(Grid header, string marker)
    {
        var column = HeaderColumn(header, marker);
        var width = header.ColumnDefinitions[column].Width;
        Assert.True(width.IsAbsolute, $"{marker} column is {width}, expected an absolute width");
        return width.Value;
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
