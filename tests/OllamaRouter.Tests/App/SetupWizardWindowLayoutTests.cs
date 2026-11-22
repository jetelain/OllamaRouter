using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using OllamaRouter.App;
using Avalonia.Data;
using Avalonia.Headless;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.App.Views.Wizard;
using OllamaRouter.Endpoints.RouterApi.Fleet;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Setup Wizard window layout contract tests (task #150, extended by task #151):
/// the branding header (logo + "OllamaRouter") must be the FIRST top-docked child of the
/// wizard window — directly ABOVE the 5-step strip — and the Step 1 view must declare both
/// section cards ("Ollama", "Configuration for router"). Task #160 unifies Step 1 Section 2
/// into an aligned row grid — every row = label left + ✓-state/action button right (the
/// detected LAN-exposure row, the Ollama port row, the :11434 router row) — with NO CheckBox
/// anywhere in Step 1, plus the Linux manual block. Task #151 reworks the Step 2 pane (fleet
/// table with hardware headers + the main app's dedicated add-dialog buttons, the remote form
/// gone) and adds the Step 3 pane ("Ollama Cloud" + "Other providers" cards). Structural
/// assertions only — no measure/arrange, because the test project never bootstraps fonts.
/// <see cref="SetupWizardWindow"/> is a <see cref="Window"/>, so construction and the
/// assertions are dispatched on a headless Avalonia session (Avalonia controls are
/// dispatcher-affine) — the same pattern as <c>PreloadDialogLayoutTests</c>. Reuses the
/// file-level <see cref="HeadlessLayoutTestApp"/> host (no second Application host class)
/// and joins the exclusive "Avalonia" xunit collection.
/// </summary>
[Collection("Avalonia")]
public sealed class SetupWizardWindowLayoutTests : IClassFixture<SetupWizardWindowLayoutTests.HeadlessSessionFixture>
{
    private readonly HeadlessSessionFixture _fixture;

    public SetupWizardWindowLayoutTests(HeadlessSessionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Root_FirstTopDockedChild_IsBrandingHeader_LogoAndName()
    {
        await _fixture.DispatchAsync(() =>
        {
            var dock = GetWindowDock();

            // Child 0: the branding header Border (DockPanel.Dock="Top", above the step strip).
            var header = Assert.IsType<Border>(dock.Children[0]);
            Assert.Equal(Dock.Top, DockPanel.GetDock(header));
            var row = Assert.IsType<StackPanel>(header.Child);
            var logo = Assert.IsType<Image>(row.Children[0]);
            var name = Assert.IsType<TextBlock>(row.Children[1]);

            Assert.NotNull(logo.Source);
            // A non-null Bitmap proves the avares://ollamarouter-app/Assets/icon.ico URI
            // resolved to the app icon asset (the same URI form as the window Icon).
            Assert.IsType<Avalonia.Media.Imaging.Bitmap>(logo.Source);
            Assert.Equal("OllamaRouter", name.Text);
        });
    }

    [Fact]
    public async Task Root_StepStrip_DirectlyBelowHeader_KeepsFiveStepLabels()
    {
        await _fixture.DispatchAsync(() =>
        {
            var dock = GetWindowDock();

            // Child 1: the step strip, directly below the branding header (regression).
            var strip = Assert.IsType<Border>(dock.Children[1]);
            Assert.Equal(Dock.Top, DockPanel.GetDock(strip));
            var labels = Assert.IsType<StackPanel>(strip.Child).Children.OfType<TextBlock>().ToArray();
            Assert.Equal(
                ["1 · Local Ollama", "2 · Fleet Targets", "3 · Cloud & Providers", "4 · Micro-Benchmark", "5 · Recommendations"],
                labels.Select(t => t.Text));
        });
    }

    /// <summary>
    /// Task #151 Step 2 pane: the fleet table header declares the hardware columns (GPU /
    /// VRAM / Bandwidth), the add buttons are EXACTLY the main app's dedicated dialog
    /// buttons ("Add Ollama Node" / "Add Provider"), and the removed controls (the
    /// "Add Local node" button, the "Toggle Cloud" checkbox, the remote-form TextBoxes) are
    /// gone.
    /// </summary>
    [Fact]
    public async Task Step2_FleetTable_HardwareHeaders_AndDialogButtonsOnly()
    {
        await _fixture.DispatchAsync(() =>
        {
            var stepView = GetStep2View();
            var texts = stepView.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();

            // The hardware table header labels (task #151).
            Assert.Contains("Name", texts);
            Assert.Contains("URL", texts);
            Assert.Contains("GPU", texts);
            Assert.Contains("VRAM", texts);
            Assert.Contains("Bandwidth", texts);
            Assert.Contains("Liveness", texts);
            Assert.Contains("Enabled", texts);

            // EXACTLY the two dedicated main-app dialog buttons (depth-first document
            // order) — the removed "Add Local node" / "Toggle Cloud" buttons are gone.
            var buttons = stepView.GetLogicalDescendants().OfType<Button>()
                .Select(b => (string?)b.Content)
                .ToArray();
            Assert.Equal(["Add Ollama Node", "Add Provider"], buttons);

            // The removed inline remote form: no TextBoxes; the removed cloud-overflow
            // toggle: no CheckBox on this step.
            Assert.Empty(stepView.GetLogicalDescendants().OfType<TextBox>());
            Assert.Empty(stepView.GetLogicalDescendants().OfType<CheckBox>());
        });
    }

    /// <summary>
    /// Task #151 Step 3 pane: the "Ollama Cloud" card (the fleet-tab "Enabled" convention —
    /// label + state CheckBox + command-bound "Toggle Cloud" button — and the cloud-model
    /// dropdown bound to CloudModels/SelectedCloudModel) and the "Other providers" card
    /// with its "Add Provider" button.
    /// </summary>
    [Fact]
    public async Task Step3_CloudAndProvidersCards_DeclareToggleDropdownAndAddProvider()
    {
        await _fixture.DispatchAsync(() =>
        {
            var (stepView, root) = GetStep3View();
            var cards = stepView.GetLogicalDescendants().OfType<Border>()
                .Where(b => GetSectionHeader(b) is "Ollama Cloud" or "Other providers")
                .ToList();
            // Each header appears exactly once (Assert.Single below) — two cards in total.

            // Card "Ollama Cloud" (task #151).
            var cloudCard = Assert.Single(cards, c => GetSectionHeader(c) == "Ollama Cloud");
            Assert.Contains(
                cloudCard.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text),
                t => t == "Enabled");
            Assert.Single(cloudCard.GetLogicalDescendants().OfType<CheckBox>()); // the state CheckBox
            var toggle = Assert.Single(
                cloudCard.GetLogicalDescendants().OfType<Button>(),
                b => (string?)b.Content == "Toggle Cloud");
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(toggle, Button.CommandProperty));
            var combo = Assert.Single(cloudCard.GetLogicalDescendants().OfType<ComboBox>());
            // The dropdown's source contract (task #151): a live binding exists and its
            // resolved value IS the step view model's cloud-model collection (the fleet
            // catalog's cloud models — not some other list).
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(combo, ComboBox.ItemsSourceProperty));
            Assert.Same(root.CloudStep.CloudModels, combo.GetValue(ComboBox.ItemsSourceProperty));
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(combo, ComboBox.SelectedItemProperty));

            // Card "Other providers": the main app's dedicated provider-dialog button.
            var providersCard = Assert.Single(cards, c => GetSectionHeader(c) == "Other providers");
            var addProvider = Assert.Single(providersCard.GetLogicalDescendants().OfType<Button>());
            Assert.Equal("Add Provider", (string?)addProvider.Content);
        });
    }

    /// <summary>
    /// Task #160 Step 1 Section 2: the unified row grid — exactly three aligned rows
    /// (label left + right cell), each right cell holding exactly one of a green ✓, an action
    /// button or nothing; the old LAN-mesh CheckBox is GONE from the whole Step 1 view.
    /// Row order: LAN exposure (bound label + bound toggle button), Ollama port
    /// (5 bound state labels + ✓ / Change port), router :11434 (bound label + ✓ / Start router).
    /// </summary>
    [Fact]
    public async Task Step1_Section2_UnifiedRowGrid_NoCheckBox_LanPortRouterRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var stepView = GetStep1View();

            // AC1: NO CheckBox anywhere in Step 1 (the LAN-mesh checkbox is removed).
            Assert.Empty(stepView.GetLogicalDescendants().OfType<CheckBox>());

            // Section 1 "Ollama": Install / Start / Update / Re-check, each command-bound.
            var cards = FindSectionCards(stepView);
            var section1 = cards.Single(c => GetSectionHeader(c) == "Ollama");
            var section1Buttons = section1.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.Equal(
                ["Install Ollama", "Start Ollama", "Update Ollama", "Re-check"],
                section1Buttons.Select(b => (string?)b.Content));
            Assert.All(section1Buttons, b =>
                Assert.NotNull(BindingOperations.GetBindingExpressionBase(b, Button.CommandProperty)));

            // The unified row grid (task #160): exactly three 2-column rows — label column
            // star, right-cell column auto — across the whole Step 1 view.
            var rows = stepView.GetLogicalDescendants().OfType<Grid>()
                .Where(g => g.ColumnDefinitions.Count == 2
                    && g.ColumnDefinitions[0].Width.IsStar && g.ColumnDefinitions[1].Width.IsAuto)
                .ToList();
            Assert.Equal(3, rows.Count);

            // Row identification by right-cell content.
            var portRow = Assert.Single(rows, g => g.GetLogicalDescendants().OfType<Button>()
                .Any(b => (string?)b.Content == "Change port"));
            var routerRow = Assert.Single(rows, g => g.GetLogicalDescendants().OfType<Button>()
                .Any(b => (string?)b.Content == "Start router"));
            var lanRow = Assert.Single(rows, g => !ReferenceEquals(g, portRow) && !ReferenceEquals(g, routerRow));

            // Row 1 (LAN): the bound label + the toggle button — its Content is LIVE-bound
            // to LanToggleText (no static text: the state drives it) and Command-bound.
            Assert.Contains(lanRow.GetLogicalDescendants().OfType<TextBlock>(), t =>
                BindingOperations.GetBindingExpressionBase(t, TextBlock.TextProperty) is not null);
            var lanToggle = Assert.Single(lanRow.GetLogicalDescendants().OfType<Button>());
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(lanToggle, Button.CommandProperty));
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(lanToggle, Button.ContentProperty));

            // Rows 2+3: the action buttons are command-bound.
            Assert.All(
                portRow.GetLogicalDescendants().OfType<Button>()
                    .Concat(routerRow.GetLogicalDescendants().OfType<Button>()),
                b => Assert.NotNull(BindingOperations.GetBindingExpressionBase(b, Button.CommandProperty)));

            // The two green ✓ right cells (port row + router row): text + #8BC34A foreground.
            var checks = stepView.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => t.Text == "✓")
                .ToArray();
            Assert.Equal(2, checks.Length);
            Assert.All(checks, t =>
            {
                // Avalonia 12 parses XAML brush strings into IMMUTABLE brushes — assert via
                // the ISolidColorBrush interface (works for both mutable and immutable).
                var brush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(t.Foreground);
                Assert.Equal(Avalonia.Media.Color.Parse("#8BC34A"), brush.Color);
            });

            // Exactly 7 TextBlocks in Section 2 carry a LIVE Text binding: 1 LanRowLabel +
            // 5 OllamaPortLabel state variants + 1 RouterStatusLabel (the ✓s are literal).
            var section2 = cards.Single(c => GetSectionHeader(c) == "Configuration for router");
            Assert.Equal(7, section2.GetLogicalDescendants().OfType<TextBlock>().Count(t =>
                BindingOperations.GetBindingExpressionBase(t, TextBlock.TextProperty) is not null));

            // The Linux manual-instructions block (wording: install/update are one-click).
            Assert.Contains(
                stepView.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text),
                t => t is not null && t.StartsWith("Linux: install and update are one-click above"));
        });
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static DockPanel GetWindowDock()
    {
        var window = new SetupWizardWindow { DataContext = BuildRootViewModel() };
        return Assert.IsType<DockPanel>(window.Content);
    }

    /// <summary>
    /// The root view model the composition root would set as the window DataContext — built
    /// with fully faked seams (same wiring as the root view-model tests in
    /// <c>SetupWizardViewModelsTests</c>): no real processes, ports, network or sleeps.
    /// </summary>
    private static SetupWizardViewModel BuildRootViewModel()
    {
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var connection = SetupWizardViewModelsTests.FakeConnection(fake, null);
        var configurator = new LocalOllamaConfigurator
        {
            IsWindows = _ => true,
            IsMacOS = _ => false,
            BinaryProbe = _ => new OllamaDetection(true, "/usr/local/bin/ollama", false),
            ProcessNames = _ => [],
            PortProbe = _ => false,
            Delay = _ => Task.CompletedTask,
            OllamaHostReader = () => null,
            // Task #160: the new LAN-state seams MUST be faked (hermeticity) — window
            // construction can never touch DNS/sockets/launchctl through the refresh.
            NonLoopbackLocalAddresses = () => [],
            LanBindProbe = (_, _) => null,
            LaunchctlGetenv = _ => null,
        };
        var environment = new WizardEnvironmentStepViewModel(configurator, connection);

        Task GatedDelay(TimeSpan _, CancellationToken ct) => new Task(static () => { }, ct);

        WizardTargetsStepViewModel targets = null!;
        targets = new WizardTargetsStepViewModel(() => fake, ct => targets.RefreshAsync(ct), GatedDelay);
        // Task #151: the new Step 3 (Cloud & External Providers) — entry-refresh only (no
        // poller), so no self-referential tick wiring.
        var cloud = new WizardCloudStepViewModel(() => fake);
        WizardBenchmarkStepViewModel bench = null!;
        bench = new WizardBenchmarkStepViewModel(() => fake, ct => bench.RefreshAsync(ct), GatedDelay);
        SetupWizardViewModel root = null!;
        // Task #151: the micro-benchmark is now step index 3 (Step 4).
        var rec = new WizardRecommendationsStepViewModel(() => fake, () => _ = root.GoToAsync(3));
        root = new SetupWizardViewModel(connection, environment, targets, cloud, bench, rec);
        return root;
    }

    private static WizardEnvironmentStepView GetStep1View()
    {
        var window = new SetupWizardWindow { DataContext = BuildRootViewModel() };
        var dock = Assert.IsType<DockPanel>(window.Content);
        // The step body is the LAST DockPanel child (fill); child 0 of its Grid is the
        // Step 1 pane (SelectedStep defaults to 0).
        var bodyGrid = Assert.IsType<Grid>(dock.Children[^1]);
        var step1Pane = Assert.IsType<Border>(bodyGrid.Children[0]);
        return Assert.IsType<WizardEnvironmentStepView>(step1Pane.Child);
    }

    private static WizardTargetsStepView GetStep2View()
    {
        var window = new SetupWizardWindow { DataContext = BuildRootViewModel() };
        var dock = Assert.IsType<DockPanel>(window.Content);
        // The step body is the LAST DockPanel child (fill); child 1 of its Grid is the
        // Step 2 (Fleet Targets) pane.
        var bodyGrid = Assert.IsType<Grid>(dock.Children[^1]);
        var step2Pane = Assert.IsType<Border>(bodyGrid.Children[1]);
        return Assert.IsType<WizardTargetsStepView>(step2Pane.Child);
    }

    private static (WizardCloudStepView View, SetupWizardViewModel Root) GetStep3View()
    {
        var root = BuildRootViewModel();
        var window = new SetupWizardWindow { DataContext = root };
        var dock = Assert.IsType<DockPanel>(window.Content);
        // The step body is the LAST DockPanel child (fill); child 2 of its Grid is the
        // Step 3 (Cloud & External Providers) pane (task #151).
        var bodyGrid = Assert.IsType<Grid>(dock.Children[^1]);
        var step3Pane = Assert.IsType<Border>(bodyGrid.Children[2]);
        return (Assert.IsType<WizardCloudStepView>(step3Pane.Child), root);
    }

    private static List<Border> FindSectionCards(Avalonia.Visual root)
    {
        // The two section cards are the Borders whose first child is a header TextBlock
        // ("Ollama" / "Configuration for router").
        return root.GetLogicalDescendants().OfType<Border>()
            .Where(b => GetSectionHeader(b) is "Ollama" or "Configuration for router")
            .ToList();
    }

    private static string? GetSectionHeader(Border border)
    {
        if (border.Child is StackPanel panel && panel.Children.Count > 0 && panel.Children[0] is TextBlock header)
        {
            return header.Text;
        }

        return null;
    }

    /// <summary>
    /// One headless Avalonia session per test class (same shape as
    /// <c>PreloadDialogLayoutTests.HeadlessSessionFixture</c>): <see cref="Window"/>
    /// construction needs a windowing platform, which the test project otherwise never
    /// bootstraps. All control-tree access happens inside <see cref="DispatchAsync{TResult}"/>
    /// because Avalonia controls are dispatcher-affine.
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
