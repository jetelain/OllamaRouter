using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Target edit dialog layout contract tests (task #144): the form grid must declare TEN
/// explicit <see cref="GridLength.Auto"/> rows (grew from eight by the SSH User / SSH Port
/// fields) and the new labels + TextBoxes must each live in their OWN declared row (8/9) —
/// the task #128 lesson: without explicit row declarations the fields clamp into the single
/// implicit row 0 and overprint the labels and watermarks. Structural assertions only — no
/// measure/arrange, because the test project never bootstraps fonts. <c>TargetEditDialog</c>
/// is a <see cref="Window"/>, so both construction and the assertions are dispatched on a
/// headless Avalonia session (the <see cref="PreloadDialogLayoutTests"/> fixture pattern).
/// Kept in the exclusive "Avalonia" xunit collection so it never runs concurrently with the
/// other Avalonia-control-creating test classes.
/// </summary>
[Collection("Avalonia")]
public sealed class TargetEditDialogLayoutTests :
    IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>
{
    private readonly PreloadDialogLayoutTests.HeadlessSessionFixture _fixture;

    public TargetEditDialogLayoutTests(PreloadDialogLayoutTests.HeadlessSessionFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task FormGrid_DeclaresTenAutoRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetFormGrid();

            Assert.Equal(10, grid.RowDefinitions.Count);
            Assert.All(grid.RowDefinitions, r => Assert.Equal(GridLength.Auto, r.Height));
        });
    }

    [Fact]
    public async Task SshUser_LabelAndTextBox_LiveInRow8()
    {
        await _fixture.DispatchAsync(() => AssertSshField(8, "SSH User:", "SshUser"));
    }

    [Fact]
    public async Task SshPort_LabelAndTextBox_LiveInRow9()
    {
        await _fixture.DispatchAsync(() => AssertSshField(9, "SSH Port:", "SshPort"));
    }

    [Fact]
    public async Task FormGrid_NoTwoChildren_ShareACell()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetFormGrid();

            // Mirror Avalonia Grid's clamping of out-of-range row indices to the last row.
            int rowCount = grid.RowDefinitions.Count;
            var cells = grid.Children
                .Select(c => (
                    Row: Math.Min(Grid.GetRow(c), rowCount - 1),
                    Column: Grid.GetColumn(c)))
                .ToArray();

            Assert.Equal(grid.Children.Count, cells.Distinct().Count());
        });
    }

    [Fact]
    public async Task SshTextBoxes_CarryTheirTextBindings()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetFormGrid();

            Assert.NotNull(BindingOperations.GetBindingExpressionBase(
                grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 8), TextBox.TextProperty));
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(
                grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 9), TextBox.TextProperty));
        });
    }

    private static void AssertSshField(int row, string expectedLabel, string expectedBindingPath)
    {
        var grid = GetFormGrid();

        var label = grid.Children.OfType<TextBlock>().Single(t => t.Text == expectedLabel);
        var textBox = grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == row);

        Assert.True(Grid.GetRow(label) < grid.RowDefinitions.Count,
            $"Label \"{expectedLabel}\" row index {Grid.GetRow(label)} is out of range for " +
            $"{grid.RowDefinitions.Count} declared rows");
        Assert.Equal(row, Grid.GetRow(label));
        Assert.Equal(0, Grid.GetColumn(label));

        Assert.True(Grid.GetRow(textBox) < grid.RowDefinitions.Count,
            $"TextBox on row {row} index {Grid.GetRow(textBox)} is out of range for " +
            $"{grid.RowDefinitions.Count} declared rows");
        Assert.Equal(row, Grid.GetRow(textBox));
        Assert.Equal(1, Grid.GetColumn(textBox));

        var expression = BindingOperations.GetBindingExpressionBase(textBox, TextBox.TextProperty);
        Assert.NotNull(expression);
        Assert.Equal(expectedBindingPath,
            expression!.GetType().GetProperty("Description")?.GetValue(expression) as string);
    }

    /// <summary>
    /// Builds the dialog (with a throwaway form, exactly as <c>FleetTargetsView</c> does in
    /// production) and returns its form grid: <c>dialog.Content</c> →
    /// <see cref="DockPanel"/> → child 2 (the fill <see cref="ScrollViewer"/>) → its
    /// <see cref="StackPanel"/> content → child 0 (the form <see cref="Grid"/>).
    /// </summary>
    private static Grid GetFormGrid()
    {
        var form = new TargetEditFormViewModel(isEdit: false);
        var dialog = new TargetEditDialog(form);
        HeadlessThemeHelper.Apply(dialog);

        var dock = Assert.IsType<DockPanel>(dialog.Content);
        var scrollViewer = Assert.IsType<ScrollViewer>(dock.Children[2]);
        var stack = Assert.IsType<StackPanel>(scrollViewer.Content);
        return Assert.IsType<Grid>(stack.Children[0]);
    }
}
