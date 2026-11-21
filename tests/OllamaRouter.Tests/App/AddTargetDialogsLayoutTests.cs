using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Layout contract tests for the dedicated per-type target add dialogs (task #149): each
/// dialog's form grid declares its explicit <see cref="GridLength.Auto"/> rows (FIVE for
/// the Ollama node, FOUR for the external provider — the task #128 lesson: without explicit
/// row declarations the fields clamp into the single implicit row 0 and overprint), every
/// label + TextBox lives in its OWN declared row/column, the binding paths are preserved,
/// no two children share a cell, the API key box is masked, the four preset chips are wired
/// to <c>ApplyPresetCommand</c> with the right parameters, and the NEGATIVE field inventory
/// holds (the Ollama dialog exposes no Type/Provider/ApiKey/Power/Models control; the
/// provider dialog exposes no Type/SSH/MaxParallel control). Structural assertions only —
/// no measure/arrange (the test project never bootstraps fonts). Both dialogs are
/// <see cref="Window"/>, so construction and the assertions are dispatched on a headless
/// Avalonia session (the <see cref="PreloadDialogLayoutTests"/> fixture pattern), in the
/// exclusive "Avalonia" xunit collection.
/// </summary>
[Collection("Avalonia")]
public sealed class AddTargetDialogsLayoutTests :
    IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>
{
    private readonly PreloadDialogLayoutTests.HeadlessSessionFixture _fixture;

    public AddTargetDialogsLayoutTests(PreloadDialogLayoutTests.HeadlessSessionFixture fixture) =>
        _fixture = fixture;

    // -- Window shell (both dialogs) ----------------------------------------------------

    [Fact]
    public async Task Dialogs_CarryTheirTitles_SaveCancelAndTheErrorBanner()
    {
        await _fixture.DispatchAsync(() =>
        {
            foreach (var (title, dialog) in new (string, Window)[]
            {
                ("Add Ollama Node", new AddOllamaNodeDialog(new OllamaNodeAddFormViewModel())),
                ("Add External Provider", new AddExternalProviderDialog(new ExternalProviderAddFormViewModel())),
            })
            {
                Assert.Equal(title, dialog.Title);

                var (_, buttons, _, _) = Traverse(dialog);
                var contents = buttons.Children.OfType<Button>().Select(b => b.Content as string).ToArray();
                Assert.Equal(new[] { "Cancel", "Save" }, contents);

                // The top-docked error banner binds the form's validation message + visibility.
                var banner = Assert.IsType<TextBlock>(
                    Assert.IsType<DockPanel>(dialog.Content).Children[0]);
                Assert.Equal("ValidationErrorMessage", BindingPath(banner, TextBlock.TextProperty));
                Assert.Equal("HasValidationError", BindingPath(banner, Control.IsVisibleProperty));
            }
        });
    }

    // -- Add Ollama Node dialog ----------------------------------------------------------

    [Fact]
    public async Task OllamaDialog_FormGrid_DeclaresFiveAutoRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetOllamaGrid();

            Assert.Equal(5, grid.RowDefinitions.Count);
            Assert.All(grid.RowDefinitions, r => Assert.Equal(GridLength.Auto, r.Height));
        });
    }

    [Theory]
    [InlineData(0, "Name:", "Name")]
    [InlineData(1, "URL:", "Url")]
    [InlineData(2, "Max Parallel:", "MaxParallel")]
    [InlineData(3, "SSH User:", "SshUser")]
    [InlineData(4, "SSH Port:", "SshPort")]
    public async Task OllamaDialog_FormFields_EachLabelAndTextBox_LiveInItsOwnRow(
        int row, string expectedLabel, string expectedBindingPath)
    {
        await _fixture.DispatchAsync(() => AssertFieldInRow(GetOllamaGrid(), row, expectedLabel, expectedBindingPath));
    }

    [Fact]
    public async Task OllamaDialog_FormGrid_NoTwoChildren_ShareACell()
    {
        await _fixture.DispatchAsync(() => AssertNoSharedCell(GetOllamaGrid()));
    }

    [Fact]
    public async Task OllamaDialog_TestConnectionCheckbox_IsBelowTheFormGrid()
    {
        await _fixture.DispatchAsync(() =>
        {
            var (_, _, _, content) = GetOllamaParts();
            var checkBox = Assert.IsType<CheckBox>(content.Children[1]);

            Assert.Equal("TestConnectionFirst", BindingPath(checkBox, CheckBox.IsCheckedProperty));
        });
    }

    [Fact]
    public async Task OllamaDialog_NegativeFieldInventory_NoTypeProviderKeyPowerOrModels()
    {
        await _fixture.DispatchAsync(() =>
        {
            var dialog = new AddOllamaNodeDialog(new OllamaNodeAddFormViewModel());
            var (grid, _, _, _) = GetOllamaParts();

            // No type dropdown and no openai-only / shared-only field of any kind.
            Assert.Empty(AllControls(dialog).OfType<ComboBox>());

            var labels = grid.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.DoesNotContain(labels, l =>
                l is "Type:" or "Provider:" or "API Key:" or "Power (W):" or "Hardware Hint:");

            // The Ollama form has NO masked key box (an apiKey on a non-openai target is a 400).
            Assert.All(grid.Children.OfType<TextBox>(), tb => Assert.Equal(default, tb.PasswordChar));

            // No declared-models roster editor.
            Assert.Empty(AllControls(dialog).OfType<ItemsControl>());
        });
    }

    // -- Add External Provider dialog -----------------------------------------------------

    [Fact]
    public async Task ProviderDialog_FormGrid_DeclaresFourAutoRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetProviderGrid();

            Assert.Equal(4, grid.RowDefinitions.Count);
            Assert.All(grid.RowDefinitions, r => Assert.Equal(GridLength.Auto, r.Height));
        });
    }

    [Theory]
    [InlineData(0, "Name:", "Name")]
    [InlineData(1, "Base URL:", "BaseUrl")]
    [InlineData(2, "API Key:", "ApiKey")]
    [InlineData(3, "Provider:", "Provider")]
    public async Task ProviderDialog_FormFields_EachLabelAndTextBox_LiveInItsOwnRow(
        int row, string expectedLabel, string expectedBindingPath)
    {
        await _fixture.DispatchAsync(() => AssertFieldInRow(GetProviderGrid(), row, expectedLabel, expectedBindingPath));
    }

    [Fact]
    public async Task ProviderDialog_ApiKeyBox_IsMasked()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetProviderGrid();
            var keyBox = grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 2);

            Assert.Equal('•', keyBox.PasswordChar); // key material is never displayed
        });
    }

    [Fact]
    public async Task ProviderDialog_FormGrid_NoTwoChildren_ShareACell()
    {
        await _fixture.DispatchAsync(() => AssertNoSharedCell(GetProviderGrid()));
    }

    [Fact]
    public async Task ProviderDialog_PresetChipRow_FourChipsWithCommandAndParameters()
    {
        await _fixture.DispatchAsync(() =>
        {
            var (_, _, _, content) = GetProviderParts();
            var chips = Assert.IsType<StackPanel>(content.Children[0]);
            var buttons = chips.Children.OfType<Button>().ToArray();

            Assert.Equal(new[] { "OpenRouter", "Groq", "OpenAI", "Custom" },
                buttons.Select(b => b.Content as string).ToArray());

            var expectedParameters = new[] { "openrouter", "groq", "openai", string.Empty };
            for (var i = 0; i < buttons.Length; i++)
            {
                var chip = buttons[i];
                Assert.Equal("ApplyPresetCommand", BindingPath(chip, Button.CommandProperty));
                Assert.Equal(expectedParameters[i], chip.GetValue(Button.CommandParameterProperty));
            }
        });
    }

    [Fact]
    public async Task ProviderDialog_PresetChipHighlight_FollowsTheAppliedPreset()
    {
        await _fixture.DispatchAsync(() =>
        {
            var form = new ExternalProviderAddFormViewModel();
            var dialog = new AddExternalProviderDialog(form);
            var (_, _, _, content) = GetParts(dialog);
            var chips = Assert.IsType<StackPanel>(content.Children[0]);
            var byContent = chips.Children.OfType<Button>().ToDictionary(b => (string)b.Content!);

            // Default (no preset applied): no chip is highlighted.
            Assert.All(byContent.Values, b => AssertBrushAlpha(b, 0));

            form.ApplyPresetCommand.Execute("groq");
            AssertBrushAlpha(byContent["Groq"], 90); // the soft accent
            AssertBrushAlpha(byContent["OpenRouter"], 0);
            AssertBrushAlpha(byContent["OpenAI"], 0);
            AssertBrushAlpha(byContent["Custom"], 0);

            form.ApplyPresetCommand.Execute(""); // Custom
            Assert.All(byContent.Values, b => AssertBrushAlpha(b, 0));
        });
    }

    [Fact]
    public async Task ProviderDialog_TestConnectionCheckbox_IsBelowTheFormGrid()
    {
        await _fixture.DispatchAsync(() =>
        {
            var (_, _, _, content) = GetProviderParts();
            var checkBox = Assert.IsType<CheckBox>(content.Children[2]);

            Assert.Equal("TestConnectionFirst", BindingPath(checkBox, CheckBox.IsCheckedProperty));
        });
    }

    [Fact]
    public async Task ProviderDialog_NegativeFieldInventory_NoTypeSshOrMaxParallel()
    {
        await _fixture.DispatchAsync(() =>
        {
            var dialog = new AddExternalProviderDialog(new ExternalProviderAddFormViewModel());
            var (grid, _, _, _) = GetProviderParts();

            // No type dropdown (type=openai is pinned) — daemon-side probing is
            // Ollama-protocol-only, and MaxParallel is not exposed in this dialog.
            Assert.Empty(AllControls(dialog).OfType<ComboBox>());

            var labels = grid.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.DoesNotContain(labels, l => l is "Type:" or "SSH User:" or "SSH Port:" or "Max Parallel:");
        });
    }

    // -- Shared helpers ------------------------------------------------------------------

    private static Grid GetOllamaGrid() => GetOllamaParts().grid;

    private static (Grid grid, StackPanel buttons, ScrollViewer scroll, StackPanel content) GetOllamaParts()
    {
        var (dock, buttons, scroll, content) = Traverse(new AddOllamaNodeDialog(new OllamaNodeAddFormViewModel()));
        var grid = Assert.IsType<Grid>(content.Children[0]);
        return (grid, buttons, scroll, content);
    }

    private static Grid GetProviderGrid() => GetProviderParts().grid;

    private static (Grid grid, StackPanel buttons, ScrollViewer scroll, StackPanel content) GetProviderParts()
    {
        var (dock, buttons, scroll, content) = Traverse(new AddExternalProviderDialog(new ExternalProviderAddFormViewModel()));
        var grid = Assert.IsType<Grid>(content.Children[1]); // [0] is the preset chip row
        return (grid, buttons, scroll, content);
    }

    private static (DockPanel dock, StackPanel buttons, ScrollViewer scroll, StackPanel content)
        Traverse(Window dialog)
    {
        var dock = Assert.IsType<DockPanel>(dialog.Content);
        var buttons = Assert.IsType<StackPanel>(dock.Children[1]); // the bottom-docked button row
        var scroll = Assert.IsType<ScrollViewer>(dock.Children[2]); // the fill
        var content = Assert.IsType<StackPanel>(scroll.Content);
        return (dock, buttons, scroll, content);
    }

    private static (StackPanel chipRow, StackPanel buttons, ScrollViewer scroll, StackPanel content)
        GetParts(Window dialog)
    {
        var (_, buttons, scroll, content) = Traverse(dialog);
        return (Assert.IsType<StackPanel>(content.Children[0]), buttons, scroll, content);
    }

    private static void AssertFieldInRow(Grid grid, int row, string expectedLabel, string expectedBindingPath)
    {
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

    private static void AssertNoSharedCell(Grid grid)
    {
        // Mirror Avalonia Grid's clamping of out-of-range row indices to the last row.
        int rowCount = grid.RowDefinitions.Count;
        var cells = grid.Children
            .Select(c => (
                Row: Math.Min(Grid.GetRow(c), rowCount - 1),
                Column: Grid.GetColumn(c)))
            .ToArray();

        Assert.Equal(grid.Children.Count, cells.Distinct().Count());
    }

    private static void AssertBrushAlpha(Button chip, byte expectedAlpha)
    {
        var brush = Assert.IsType<ImmutableSolidColorBrush>(chip.Background);
        Assert.Equal(expectedAlpha, brush.Color.A);
    }

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
