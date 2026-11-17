using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Preload Model dialog layout contract tests (task #128): the form grid must declare three
/// explicit <see cref="GridLength.Auto"/> rows so the Target Pin / Keep Alive / Context Intent
/// fields stack into distinct rows instead of clamping into the single implicit row 0, which
/// overprinted the labels and watermarks. Structural assertions only — no measure/arrange,
/// because the test project never bootstraps fonts. <c>PreloadDialog</c> is a
/// <see cref="Window"/>, so both construction and the assertions are dispatched on a headless
/// Avalonia session (Avalonia controls are dispatcher-affine). Kept in the "Avalonia" xunit
/// collection (task #129) so it never runs concurrently with the other Avalonia-control-creating
/// test classes (shared process-wide dispatcher/property-pool state).
/// </summary>
[Collection("Avalonia")]
public sealed class PreloadDialogLayoutTests : IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>
{
    private readonly HeadlessSessionFixture _fixture;

    public PreloadDialogLayoutTests(HeadlessSessionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FormGrid_DeclaresThreeAutoRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetFormGrid();

            Assert.Equal(3, grid.RowDefinitions.Count);
            Assert.All(grid.RowDefinitions, r => Assert.Equal(GridLength.Auto, r.Height));
        });
    }

    [Theory]
    [InlineData(0, "Target Pin:")]
    [InlineData(1, "Keep Alive (s):")]
    [InlineData(2, "Context Intent (tok):")]
    public async Task FormFields_EachLabelAndTextBox_LiveInItsOwnRow(int row, string expectedLabel)
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetFormGrid();

            var label = grid.Children.OfType<TextBlock>().Single(t => t.Text == expectedLabel);
            var textBox = grid.Children.OfType<TextBox>().ElementAt(row);

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
        });
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
    public async Task FormGrid_BindingsAndCommands_Preserved()
    {
        await _fixture.DispatchAsync(() =>
        {
            var (grid, stack) = GetFormGridAndStack();

            foreach (var textBox in grid.Children.OfType<TextBox>())
            {
                Assert.NotNull(BindingOperations.GetBindingExpressionBase(textBox, TextBox.TextProperty));
            }

            var buttonRow = Assert.IsType<StackPanel>(stack.Children[2]);
            var buttons = buttonRow.Children.OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Length);
            Assert.All(buttons, b =>
                Assert.NotNull(BindingOperations.GetBindingExpressionBase(b, Button.CommandProperty)));
        });
    }

    /// <summary>
    /// Builds the dialog (with a throwaway view model, exactly as <c>CartographyView</c> does
    /// in production) and returns its form grid: <c>dialog.Content</c> → top-docked
    /// <see cref="DockPanel"/> → its child 0 (the <c>DockPanel.Dock="Top"</c>
    /// <see cref="StackPanel"/>, children [0] model-name header, [1] form Grid, [2] button
    /// row, [3] error message) → child 1 (the form <see cref="Grid"/>).
    /// </summary>
    private static Grid GetFormGrid() => GetFormGridAndStack().grid;

    private static (Grid grid, StackPanel stack) GetFormGridAndStack()
    {
        var viewModel = new PreloadDialogViewModel("test-model", () => new FakeRouterApiClient());
        var dialog = new PreloadDialog(viewModel);

        var dock = Assert.IsType<DockPanel>(dialog.Content);
        var stack = Assert.IsType<StackPanel>(dock.Children[0]);
        var grid = Assert.IsType<Grid>(stack.Children[1]);
        return (grid, stack);
    }

    /// <summary>
    /// One headless Avalonia session per test class: <see cref="Window"/> construction needs
    /// a windowing platform, which the test project otherwise never bootstraps. All
    /// control-tree access happens inside <see cref="DispatchAsync{TResult}"/> because
    /// Avalonia controls are dispatcher-affine.
    /// </summary>
    public sealed class HeadlessSessionFixture : IAsyncLifetime
    {
        private HeadlessUnitTestSession? _session;

        public async Task InitializeAsync()
        {
            _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessLayoutTestApp));
        }

        public async Task DisposeAsync()
        {
            if (_session is not null)
            {
                await _session.DisposeAsync();
            }
        }

        public Task DispatchAsync(Action action) =>
            Session.Dispatch(action, CancellationToken.None);

        public Task<TResult> DispatchAsync<TResult>(Func<TResult> func) =>
            Session.Dispatch(func, CancellationToken.None);

        private HeadlessUnitTestSession Session => _session!;
    }
}

/// <summary>
/// The EXCLUSIVE "Avalonia" xunit collection (task #130 addendum): the headless
/// <see cref="HeadlessUnitTestSession"/> installs PROCESS-WIDE Avalonia statics (notably
/// <c>Application.Current</c> = the session app). With only a <c>[Collection("Avalonia")]</c>
/// group and no definition, this collection used to run IN PARALLEL with every other test
/// class; a concurrent view-model test's dispatcher post then landed on the (non-pumping)
/// headless session dispatcher and its fire-and-forget work never settled. Disabling
/// parallelization keeps the headless window disjoint from all other classes in the process.
/// </summary>
[CollectionDefinition("Avalonia", DisableParallelization = true)]
public sealed class AvaloniaCollectionDefinition;

/// <summary>Minimal Avalonia application host for the headless layout test session.</summary>
internal sealed class HeadlessLayoutTestApp : Application;
