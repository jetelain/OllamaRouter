using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Layout contract tests for the dedicated per-type target EDIT dialogs (task #171): the
/// retired monolithic <c>TargetEditDialog</c> is replaced by <see cref="EditOllamaNodeDialog"/>
/// (SEVEN <see cref="GridLength.Auto"/> rows: Name / URL / Max Parallel / Hardware Hint /
/// Power (W) / SSH User / SSH Port) and <see cref="EditExternalProviderDialog"/> (FIVE rows:
/// Name / Base URL / Provider / Max Parallel / API Key + a second roster card). Both use the
/// NightCodeForge card layout (<c>CornerRadius="8"</c>, <c>NightCodeBgSurface</c> cards,
/// <c>SemiColorBorder</c> borders), a <c>SemiColorDangerLight</c> severity banner, and
/// <c>PlaceholderText</c> only (never the obsolete <c>Watermark</c>). The Name box is a
/// DISABLED <see cref="TextBox"/> (read-only identity — D5); there is NO <c>Type:</c>
/// ComboBox (D3), NO <c>TestConnectionFirst</c> CheckBox (D4 — dead in edit mode), and the
/// NEGATIVE field inventories hold (the Ollama dialog exposes no key/provider/roster
/// control; the provider dialog exposes no SSH/power/hardware-hint control). Structural
/// assertions only — no measure/arrange (the test project never bootstraps fonts). Both
/// dialogs are <see cref="Window"/>, so construction and the assertions are dispatched on a
/// headless Avalonia session (the <see cref="PreloadDialogLayoutTests"/> fixture pattern),
/// in the exclusive "Avalonia" xunit collection.
/// </summary>
[Collection("Avalonia")]
public sealed class EditTargetDialogsLayoutTests :
    IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>
{
    private readonly PreloadDialogLayoutTests.HeadlessSessionFixture _fixture;

    public EditTargetDialogsLayoutTests(PreloadDialogLayoutTests.HeadlessSessionFixture fixture) =>
        _fixture = fixture;

    // -- Window shell (both dialogs) ----------------------------------------------------

    [Fact]
    public async Task Dialogs_CarryTheirTitles_SaveCancelAndTheSeverityBanner()
    {
        await _fixture.DispatchAsync(() =>
        {
            var dialogs = new (string, Window)[]
            {
                ("Edit Ollama Node", BuildOllamaDialog().dialog),
                ("Edit External Provider", BuildProviderDialog().dialog),
            };
            foreach (var (title, dialog) in dialogs)
            {
                Assert.Equal(title, dialog.Title);
                Assert.Equal(new Size(640, 640), new Size(dialog.Width, dialog.Height));

                var (_, buttons, _, _) = Traverse(dialog);
                var contents = buttons.Children.OfType<Button>().Select(b => b.Content as string).ToArray();
                Assert.Equal(new[] { "Cancel", "Save" }, contents);
                // The Save button is the primary call-to-action.
                Assert.Contains(buttons.Children.OfType<Button>(),
                    b => b.Classes.Contains("Primary") && (b.Content as string) == "Save");

                // The severity banner is a Border (not a bare TextBlock): IsVisible is bound
                // to HasValidationError and it contains the ValidationErrorMessage TextBlock.
                var banner = Assert.IsType<Border>(Assert.IsType<DockPanel>(dialog.Content).Children[0]);
                Assert.Equal("HasValidationError", BindingPath(banner, Control.IsVisibleProperty));
                var message = Assert.IsType<TextBlock>(banner.Child);
                Assert.Equal("ValidationErrorMessage", BindingPath(message, TextBlock.TextProperty));
            }
        });
    }

    // -- Edit Ollama Node dialog ----------------------------------------------------------

    [Fact]
    public async Task OllamaDialog_CardHasNightCodeForgeStyling_AndSevenAutoRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetOllamaFormGrid();

            Assert.Equal(7, grid.RowDefinitions.Count);
            Assert.All(grid.RowDefinitions, r => Assert.Equal(GridLength.Auto, r.Height));

            // The form card: CornerRadius 8 + the NightCodeBgSurface background.
            var card = Assert.IsType<Border>(grid.Parent);
            Assert.Equal(new CornerRadius(8), card.CornerRadius);
            Assert.NotNull(card.Background);
        });
    }

    [Theory]
    [InlineData(0, "Name:", "Name")]
    [InlineData(1, "URL:", "Url")]
    [InlineData(2, "Max Parallel:", "MaxParallel")]
    [InlineData(3, "Hardware Hint:", "HardwareHint")]
    [InlineData(4, "Power (W):", "PowerWatts")]
    [InlineData(5, "SSH User:", "SshUser")]
    [InlineData(6, "SSH Port:", "SshPort")]
    public async Task OllamaDialog_FormFields_EachLabelAndTextBox_LiveInItsOwnRow(
        int row, string expectedLabel, string expectedBindingPath)
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetOllamaFormGrid();
            AssertFieldInRow(grid, row, expectedLabel);

            var textBox = grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == row);
            Assert.Equal(expectedBindingPath, BindingPath(textBox, TextBox.TextProperty));
            // PlaceholderText only (the obsolete Watermark is gone — no AVLN5001).
            Assert.False(string.IsNullOrWhiteSpace(textBox.PlaceholderText));
        });
    }

    [Fact]
    public async Task OllamaDialog_NameBox_IsDisabled_AndOneWay()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetOllamaFormGrid();
            var nameBox = grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 0);

            Assert.False(nameBox.IsEnabled); // D5: read-only identity
            // The OneWay binding to the get-only Name property — the strict XAML compiler
            // enforces the OneWay mode (a two-way binding to a get-only property would not
            // compile), so the runtime check asserts the live binding + path only.
            var expression = BindingOperations.GetBindingExpressionBase(nameBox, TextBox.TextProperty);
            Assert.IsAssignableFrom<Avalonia.Data.BindingExpressionBase>(expression);
            Assert.Equal("Name", BindingPath(nameBox, TextBox.TextProperty));
        });
    }

    [Fact]
    public async Task OllamaDialog_FormGrid_NoTwoChildren_ShareACell()
    {
        await _fixture.DispatchAsync(() => AssertNoSharedCell(GetOllamaFormGrid()));
    }

    [Fact]
    public async Task OllamaDialog_NegativeFieldInventory_NoKeyProviderTypeModelsOrProbeCheckbox()
    {
        await _fixture.DispatchAsync(() =>
        {
            var (dialog, _) = BuildOllamaDialog();
            var grid = GetOllamaFormGrid(dialog);

            // No type dropdown (D3) and no openai-only / roster field of any kind.
            Assert.Empty(AllControls(dialog).OfType<ComboBox>());
            Assert.Empty(AllControls(dialog).OfType<ItemsControl>());
            Assert.Empty(AllControls(dialog).OfType<CheckBox>()); // D4: no TestConnectionFirst

            var labels = grid.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.DoesNotContain(labels, l => l is "Type:" or "Provider:" or "API Key:");
            Assert.DoesNotContain("Declared Models",
                AllControls(dialog).OfType<TextBlock>().Select(t => t.Text));

            // No masked key box (an apiKey on a non-openai target is a 400).
            Assert.All(grid.Children.OfType<TextBox>(), tb => Assert.Equal(default, tb.PasswordChar));
        });
    }

    // -- Edit External Provider dialog -----------------------------------------------------

    [Fact]
    public async Task ProviderDialog_CardHasNightCodeForgeStyling_AndFiveAutoRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetProviderFormGrid();

            Assert.Equal(5, grid.RowDefinitions.Count);
            Assert.All(grid.RowDefinitions, r => Assert.Equal(GridLength.Auto, r.Height));

            var card = Assert.IsType<Border>(grid.Parent);
            Assert.Equal(new CornerRadius(8), card.CornerRadius);
            Assert.NotNull(card.Background);
        });
    }

    [Theory]
    [InlineData(0, "Name:")]
    [InlineData(1, "Base URL:")]
    [InlineData(2, "Provider:")]
    [InlineData(3, "Max Parallel:")]
    [InlineData(4, "API Key:")]
    public async Task ProviderDialog_FormFields_EachLabel_LivesInItsOwnRow(int row, string expectedLabel)
    {
        await _fixture.DispatchAsync(() => AssertFieldInRow(GetProviderFormGrid(), row, expectedLabel));
    }

    [Fact]
    public async Task ProviderDialog_FormTextBoxes_CarryTheirBindingsAndPlaceholders()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetProviderFormGrid();

            Assert.Equal("Name", BindingPath(grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 0), TextBox.TextProperty));
            Assert.Equal("BaseUrl", BindingPath(grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 1), TextBox.TextProperty));
            Assert.Equal("Provider", BindingPath(grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 2), TextBox.TextProperty));
            Assert.Equal("MaxParallel", BindingPath(grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 3), TextBox.TextProperty));
            Assert.All(grid.Children.OfType<TextBox>(),
                tb => Assert.False(string.IsNullOrWhiteSpace(tb.PlaceholderText)));
        });
    }

    [Fact]
    public async Task ProviderDialog_NameBox_IsDisabled_AndOneWay()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetProviderFormGrid();
            var nameBox = grid.Children.OfType<TextBox>().Single(tb => Grid.GetRow(tb) == 0);

            Assert.False(nameBox.IsEnabled); // D5: read-only identity
            var expression = BindingOperations.GetBindingExpressionBase(nameBox, TextBox.TextProperty);
            Assert.IsAssignableFrom<Avalonia.Data.BindingExpressionBase>(expression);
            Assert.Equal("Name", BindingPath(nameBox, TextBox.TextProperty));
        });
    }

    [Fact]
    public async Task ProviderDialog_ApiKeyRow_MaskedBoxAndStatusLine()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetProviderFormGrid();
            var keyPanel = Assert.IsType<StackPanel>(
                grid.Children.Single(c => Grid.GetRow(c) == 4 && Grid.GetColumn(c) == 1));
            var keyBox = Assert.IsType<TextBox>(keyPanel.Children[0]);
            var statusLine = Assert.IsType<TextBlock>(keyPanel.Children[1]);

            Assert.NotEqual(default, keyBox.PasswordChar); // key material is never displayed
            Assert.Equal("ApiKey", BindingPath(keyBox, TextBox.TextProperty));
            Assert.Equal("ApiKeyStatusText", BindingPath(statusLine, TextBlock.TextProperty));
        });
    }

    [Fact]
    public async Task ProviderDialog_FormGrid_NoTwoChildren_ShareACell()
    {
        await _fixture.DispatchAsync(() => AssertNoSharedCell(GetProviderFormGrid()));
    }

    [Fact]
    public async Task ProviderDialog_RosterCard_HeaderItemsControlAndAddButton()
    {
        await _fixture.DispatchAsync(async () =>
        {
            var (dialog, vm) = BuildProviderDialog();
            try
            {
                var (_, _, _, content) = Traverse(dialog);
                var rosterCard = Assert.IsType<Border>(content.Children[1]);
                var rosterStack = Assert.IsType<StackPanel>(rosterCard.Child);

                Assert.Equal("Declared Models", ((TextBlock)rosterStack.Children[0]).Text);
                var items = rosterStack.Children.OfType<ItemsControl>().Single();
                Assert.Equal("Models", BindingPath(items, ItemsControl.ItemsSourceProperty));

                // The "+ Add Model" button drives the form's AddModelRowCommand.
                var addButton = rosterStack.Children.OfType<Button>().Single();
                Assert.Equal("+ Add Model", addButton.Content as string);
                Assert.Equal("AddModelRowCommand", BindingPath(addButton, Button.CommandProperty));

                // The row template: three TextBoxes (name / contextLength / quantization)
                // + the Danger "Remove" button. Built OFF-TREE via ItemTemplate.Build —
                // the DataTemplate's Content is a deferred (PointerDeferredContent) wrapper
                // until built, so reading it raw does not yield the Grid (task #173 fix —
                // the old reflection cast was vacuous: it ran in an async-void dispatch
                // whose failures were swallowed).
                var template = Assert.IsAssignableFrom<Avalonia.Controls.Templates.IDataTemplate>(items.ItemTemplate);
                var rowGrid = Assert.IsType<Grid>(template.Build(new ModelRosterEntry
                {
                    Name = "probe",
                    ContextLength = string.Empty,
                    Quantization = string.Empty,
                }));
                var rowBoxes = rowGrid.Children.OfType<TextBox>().ToArray();
                Assert.Equal(3, rowBoxes.Length);
                Assert.All(rowBoxes, tb => Assert.False(string.IsNullOrWhiteSpace(tb.PlaceholderText)));
                var removeButton = rowGrid.Children.OfType<Button>().Single();
                Assert.Equal("Remove", removeButton.Content as string);
                Assert.Contains("Danger", removeButton.Classes);
            }
            finally
            {
                await vm.DisposeAsync();
            }
        });
    }

    [Fact]
    public async Task ProviderDialog_NegativeFieldInventory_NoSshPowerHintTypeOrProbeCheckbox()
    {
        await _fixture.DispatchAsync(async () =>
        {
            var (dialog, vm) = BuildProviderDialog();
            try
            {
                var grid = GetProviderFormGrid(dialog);

                Assert.Empty(AllControls(dialog).OfType<ComboBox>()); // D3
                Assert.Empty(AllControls(dialog).OfType<CheckBox>()); // D4: no TestConnectionFirst

                var labels = grid.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
                Assert.DoesNotContain(labels,
                    l => l is "Type:" or "SSH User:" or "SSH Port:" or "Hardware Hint:" or "Power (W):");
            }
            finally
            {
                await vm.DisposeAsync();
            }
        });
    }

    // -- Shared helpers ------------------------------------------------------------------

    /// <summary>
    /// Builds the Ollama edit dialog from a SeedFleet-style row via the SAME factory the
    /// view uses (the Local row: ollama + RT 4090 hint + key flag + power override + ssh).
    /// </summary>
    private static (EditOllamaNodeDialog dialog, FleetTargetsViewModel vm) BuildOllamaDialog()
    {
        var vm = new FleetTargetsViewModel(() => new FakeRouterApiClient());
        var row = new FleetTargetRow
        {
            Name = "Local",
            Url = "http://127.0.0.1:11434/",
            Type = "ollama",
            ConfigMaxParallel = 2,
            HardwareHint = "RTX 4090",
            PowerWatts = 350,
            SshUser = "gpuadmin",
            SshPort = 2222,
            ApiKeyConfigured = true,
            Models = [new OllamaRouter.Endpoints.RouterApi.Targets.TargetModelDto("llama3:8b", 8192, "Q4_K_M")],
        };
        var dialog = new EditOllamaNodeDialog(vm.CreateOllamaNodeEditForm(row));
        HeadlessThemeHelper.Apply(dialog);
        return (dialog, vm);
    }

    /// <summary>
    /// Builds the provider edit dialog from a SeedFleet-style row via the SAME factory the
    /// view uses (the OpenRouter row: openai + provider label + key + roster).
    /// </summary>
    private static (EditExternalProviderDialog dialog, FleetTargetsViewModel vm) BuildProviderDialog()
    {
        var vm = new FleetTargetsViewModel(() => new FakeRouterApiClient());
        var row = new FleetTargetRow
        {
            Name = "OpenRouter",
            Url = "https://openrouter.ai/api",
            Type = "openai",
            Provider = "OpenRouter",
            ConfigMaxParallel = 1,
            ApiKeyConfigured = true,
            Models = [new OllamaRouter.Endpoints.RouterApi.Targets.TargetModelDto("openrouter/gpt-4o-mini")],
        };
        var dialog = new EditExternalProviderDialog(vm.CreateExternalProviderEditForm(row));
        HeadlessThemeHelper.Apply(dialog);
        return (dialog, vm);
    }

    private static Grid GetOllamaFormGrid() => GetOllamaFormGrid(BuildOllamaDialog().dialog);

    private static Grid GetOllamaFormGrid(Window dialog)
    {
        var (_, _, _, content) = Traverse(dialog);
        var card = Assert.IsType<Border>(content.Children[0]);
        return Assert.IsType<Grid>(card.Child);
    }

    private static Grid GetProviderFormGrid() => GetProviderFormGrid(BuildProviderDialog().dialog);

    private static Grid GetProviderFormGrid(Window dialog)
    {
        var (_, _, _, content) = Traverse(dialog);
        var card = Assert.IsType<Border>(content.Children[0]);
        return Assert.IsType<Grid>(card.Child);
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

    private static void AssertFieldInRow(Grid grid, int row, string expectedLabel)
    {
        var label = grid.Children.OfType<TextBlock>().Single(t => t.Text == expectedLabel);

        Assert.True(Grid.GetRow(label) < grid.RowDefinitions.Count,
            $"Label \"{expectedLabel}\" row index {Grid.GetRow(label)} is out of range for " +
            $"{grid.RowDefinitions.Count} declared rows");
        Assert.Equal(row, Grid.GetRow(label));
        Assert.Equal(0, Grid.GetColumn(label));
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
