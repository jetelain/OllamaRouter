using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.App.Views.Wizard;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Setup Wizard window layout contract tests (task #150, extended by task #151, updated for
/// NightCodeForge Installer shell parity): verifies the Grid shell (branding header with two-tone
/// OllamaRouter + daemon status badge, global recovery banner, centered 5-step strip, step body,
/// and footer), plus the structural contracts of Steps 1–5.
/// </summary>
[Collection("Avalonia")]
public sealed class SetupWizardWindowLayoutTests : IClassFixture<SetupWizardWindowLayoutTests.HeadlessSessionFixture>
{
    private readonly HeadlessSessionFixture _fixture;

    public SetupWizardWindowLayoutTests(HeadlessSessionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Root_FirstRow_IsBrandingHeader_LogoTwoToneNameAndDaemonBadge()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetWindowGrid();

            var header = Assert.IsType<Border>(grid.Children[0]);
            Assert.Equal(0, Grid.GetRow(header));
            var headerGrid = Assert.IsType<Grid>(header.Child);

            var logo = Assert.IsType<Image>(headerGrid.Children[0]);
            Assert.NotNull(logo.Source);
            Assert.IsType<Avalonia.Media.Imaging.Bitmap>(logo.Source);

            var titleStack = Assert.IsType<StackPanel>(headerGrid.Children[1]);
            var twoTone = Assert.IsType<StackPanel>(titleStack.Children[0]);
            Assert.Equal("Ollama", Assert.IsType<TextBlock>(twoTone.Children[0]).Text);
            Assert.Equal("Router", Assert.IsType<TextBlock>(twoTone.Children[1]).Text);

            var badgeBorder = Assert.IsType<Border>(headerGrid.Children[2]);
            var badgeText = Assert.IsType<TextBlock>(badgeBorder.Child);
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(badgeText, TextBlock.TextProperty));
        });
    }

    [Fact]
    public async Task Root_StepStrip_InRow2_KeepsFiveStepLabelsCentered()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetWindowGrid();

            var strip = Assert.IsType<Border>(grid.Children[2]);
            Assert.Equal(2, Grid.GetRow(strip));
            var panel = Assert.IsType<StackPanel>(strip.Child);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, panel.HorizontalAlignment);
            var labels = panel.Children.OfType<TextBlock>().ToArray();
            Assert.Equal(
                ["1 · Local Ollama", "2 · Fleet Targets", "3 · Cloud & Providers", "4 · Micro-Benchmark", "5 · Recommendations"],
                labels.Select(t => t.Text));
        });
    }

    [Fact]
    public async Task Root_Footer_InRow4_HasTitleAndBackNextFinish()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetWindowGrid();

            var footer = Assert.IsType<Border>(grid.Children[4]);
            Assert.Equal(4, Grid.GetRow(footer));
            var footerGrid = Assert.IsType<Grid>(footer.Child);
            var footerTitle = Assert.IsType<TextBlock>(footerGrid.Children[0]);
            Assert.Equal("OllamaRouter Setup Wizard", footerTitle.Text);

            var buttons = footerGrid.Children[1].GetLogicalDescendants().OfType<Button>()
                .Select(b => b.Content as string ?? string.Empty)
                .ToArray();
            Assert.Equal(["Back", "Next", "Finish"], buttons);
        });
    }

    [Fact]
    public async Task Step2_FleetTable_HardwareHeaders_AndDialogButtonsOnly()
    {
        await _fixture.DispatchAsync(() =>
        {
            var stepView = GetStepView<WizardTargetsStepView>(1);
            var texts = stepView.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();

            Assert.Contains("Name", texts);
            Assert.Contains("URL", texts);
            Assert.Contains("GPU", texts);
            Assert.Contains("VRAM", texts);
            Assert.Contains("Bandwidth", texts);
            Assert.Contains("Liveness", texts);
            Assert.Contains("Enabled", texts);

            var buttonControls = stepView.GetLogicalDescendants().OfType<Button>().ToArray();
            var buttons = buttonControls
                .Select(b => b.Content as string ?? string.Empty)
                .ToArray();
            Assert.Equal(["Add Ollama Node", "Add Provider"], buttons);
            Assert.All(buttonControls, b =>
                Assert.NotNull(BindingOperations.GetBindingExpressionBase(b, InputElement.IsEnabledProperty)));

            Assert.Empty(stepView.GetLogicalDescendants().OfType<TextBox>());
            Assert.Empty(stepView.GetLogicalDescendants().OfType<CheckBox>());
        });
    }

    [Fact]
    public async Task Step2_FleetTable_ResponsiveGridHeader_AndRowTemplate()
    {
        await _fixture.DispatchAsync(() =>
        {
            var stepView = GetStepView<WizardTargetsStepView>(1);

            var headerGrid = stepView.GetLogicalDescendants().OfType<Grid>().Single(g =>
                g.ColumnDefinitions.Count == 7
                && g.GetLogicalChildren().OfType<TextBlock>().ToArray() is { Length: 7 } labels
                && labels[0].Text == "Name"
                && labels[^1].Text == "Enabled");
            Assert.Equal(7, headerGrid.ColumnDefinitions.Count);

            var itemsControl = stepView.GetLogicalDescendants().OfType<ItemsControl>().Single();
            var template = itemsControl.ItemTemplate!.Build(new WizardTargetRow { Name = "probe", Enabled = true });
            var rowGrid = Assert.IsType<Grid>(Assert.IsType<Border>(template).Child);
            Assert.Equal(7, rowGrid.ColumnDefinitions.Count);
            Assert.Single(template!.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "enabled");
        });
    }

    [Fact]
    public async Task Step3_CloudAndProvidersCards_DeclareToggleDropdownAndAddProvider()
    {
        await _fixture.DispatchAsync(() =>
        {
            var (stepView, root) = GetStep3View();
            var cards = stepView.GetLogicalDescendants().OfType<Border>()
                .Where(b => GetSectionHeader(b) is "Ollama Cloud" or "Other providers")
                .ToList();

            var cloudCard = Assert.Single(cards, c => GetSectionHeader(c) == "Ollama Cloud");
            var cloudCheckBox = Assert.Single(cloudCard.GetLogicalDescendants().OfType<CheckBox>());
            Assert.Equal("Use Ollama Cloud when all local GPU are busy", cloudCheckBox.Content as string);
            Assert.True(cloudCheckBox.IsHitTestVisible);
            Assert.Equal("CloudEnabled", BindingPath(cloudCheckBox, CheckBox.IsCheckedProperty));
            Assert.Equal("ToggleCloudCommand", BindingPath(cloudCheckBox, Button.CommandProperty));

            var combo = Assert.Single(cloudCard.GetLogicalDescendants().OfType<ComboBox>());
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(combo, ComboBox.ItemsSourceProperty));
            Assert.Same(root.CloudStep.CloudModels, combo.GetValue(ComboBox.ItemsSourceProperty));
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(combo, ComboBox.SelectedItemProperty));

            var providersCard = Assert.Single(cards, c => GetSectionHeader(c) == "Other providers");
            var addProvider = Assert.Single(providersCard.GetLogicalDescendants().OfType<Button>());
            Assert.Equal("Add Provider", (string?)addProvider.Content);
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(addProvider, InputElement.IsEnabledProperty));

            var providersList = Assert.Single(providersCard.GetLogicalDescendants().OfType<ItemsControl>());
            Assert.Same(root.CloudStep.ExternalProviders, providersList.GetValue(ItemsControl.ItemsSourceProperty));
            var row = new WizardExternalProviderRow
            {
                Name = "OpenAI",
                Url = "https://api.openai.com/v1",
                ProviderType = "openai",
                ProviderLabel = "OpenAI API",
            };
            var providerTemplate = providersList.ItemTemplate!.Build(row);
            providerTemplate!.DataContext = row;
            Assert.Contains(
                providerTemplate.GetLogicalDescendants().OfType<TextBlock>(),
                t => t.Text == "OpenAI API");
        });
    }

    [Fact]
    public async Task Step1_Section2_UnifiedRowGrid_NoCheckBox_LanPortRouterRows()
    {
        await _fixture.DispatchAsync(() =>
        {
            var stepView = GetStepView<WizardEnvironmentStepView>(0);

            Assert.Empty(stepView.GetLogicalDescendants().OfType<CheckBox>());

            var cards = FindSectionCards(stepView);
            var section1 = cards.Single(c => GetSectionHeader(c) == "Ollama");
            var section1Buttons = section1.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.Equal(
                ["Install Ollama", "Start Ollama", "Update Ollama", "Re-check"],
                section1Buttons.Select(b => (string?)b.Content));
            Assert.All(section1Buttons, b =>
                Assert.NotNull(BindingOperations.GetBindingExpressionBase(b, Button.CommandProperty)));

            var rows = stepView.GetLogicalDescendants().OfType<Grid>()
                .Where(g => g.ColumnDefinitions.Count == 2
                    && g.ColumnDefinitions[0].Width.IsStar && g.ColumnDefinitions[1].Width.IsAuto)
                .ToList();
            Assert.Equal(3, rows.Count);

            var portRow = Assert.Single(rows, g => g.GetLogicalDescendants().OfType<Button>()
                .Any(b => (string?)b.Content == "Change port"));
            var routerRow = Assert.Single(rows, g => g.GetLogicalDescendants().OfType<Button>()
                .Any(b => (string?)b.Content == "Start router"));
            var lanRow = Assert.Single(rows, g => !ReferenceEquals(g, portRow) && !ReferenceEquals(g, routerRow));

            Assert.Contains(lanRow.GetLogicalDescendants().OfType<TextBlock>(), t =>
                BindingOperations.GetBindingExpressionBase(t, TextBlock.TextProperty) is not null);
            var lanToggle = Assert.Single(lanRow.GetLogicalDescendants().OfType<Button>());
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(lanToggle, Button.CommandProperty));
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(lanToggle, Button.ContentProperty));

            Assert.All(
                portRow.GetLogicalDescendants().OfType<Button>()
                    .Concat(routerRow.GetLogicalDescendants().OfType<Button>()),
                b => Assert.NotNull(BindingOperations.GetBindingExpressionBase(b, Button.CommandProperty)));

            var checks = stepView.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => t.Text == "✓")
                .ToArray();
            Assert.Equal(2, checks.Length);
            Assert.All(checks, t =>
            {
                var brush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(t.Foreground);
                Assert.Equal(Avalonia.Media.Color.Parse("#10B981"), brush.Color);
            });

            var section2 = cards.Single(c => GetSectionHeader(c) == "Configuration for router");
            Assert.Equal(7, section2.GetLogicalDescendants().OfType<TextBlock>().Count(t =>
                BindingOperations.GetBindingExpressionBase(t, TextBlock.TextProperty) is not null));

            Assert.Contains(
                stepView.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text),
                t => t is not null && t.StartsWith("Linux: install and update are one-click above"));
        });
    }

    [Fact]
    public async Task Step4_BenchmarkView_HasActionsSelectionHelpersAndProgressBar()
    {
        await _fixture.DispatchAsync(() =>
        {
            var stepView = GetStepView<WizardBenchmarkStepView>(3);
            var buttons = stepView.GetLogicalDescendants().OfType<Button>()
                .Select(b => b.Content as string ?? string.Empty)
                .ToArray();
            Assert.Equal(["Run benchmarks", "Clear results", "Select fitting", "Select none"], buttons);
            Assert.Contains(stepView.GetLogicalDescendants().OfType<ProgressBar>(), p => !p.IsIndeterminate);
        });
    }

    // Task #174 — the live progress row (detail text + slim fractional bar) contract:
    // the static run-status chrome renders the live summary + fractional overall bar from
    // the VM data, and the per-row DataTemplate carries the pinned live bindings.
    [Fact]
    public async Task Step4_BenchmarkView_LiveProgress_SummaryOverallBarAndRowTemplateBindings()
    {
        await _fixture.DispatchAsync(async () =>
        {
            var fake = new FakeRouterApiClient
            {
                FleetResponse = new RouterFleetResponse([]),
                BenchmarkResults = new RouterBenchmarkResultsResponse(
                [
                    new BenchmarkTargetResult("Local", BenchmarkRunStatus.Running, DateTimeOffset.UtcNow, null, [],
                        new BenchmarkTargetProgress(
                            BenchmarkPhase.DownloadingModel, "qwen3:8b", UsageRole.BasicModel, null,
                            0, 1, 0, 0, "pulling 50%", 2_306_867_200L, 4_613_734_400L,
                            "Downloading qwen3:8b (50% · 2.1 GB / 4.3 GB)", DateTimeOffset.UtcNow))
                ]),
            };
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
                NonLoopbackLocalAddresses = () => [],
                LanBindProbe = (_, _) => null,
                LaunchctlGetenv = _ => null,
            };
            var environment = new WizardEnvironmentStepViewModel(configurator, connection);

            Task GatedDelay(TimeSpan _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

            WizardTargetsStepViewModel targets = null!;
            targets = new WizardTargetsStepViewModel(() => fake, ct => targets.RefreshAsync(ct), GatedDelay);
            var cloud = new WizardCloudStepViewModel(() => fake);
            WizardBenchmarkStepViewModel bench = null!;
            bench = new WizardBenchmarkStepViewModel(() => fake, ct => bench.RefreshAsync(ct), GatedDelay);
            SetupWizardViewModel root = null!;
            var rec = new WizardRecommendationsStepViewModel(() => fake, () => _ = root.GoToAsync(3));
            root = new SetupWizardViewModel(connection, environment, targets, cloud, bench, rec);

            await bench.RefreshAsync();
            Assert.True(bench.IsBenchmarkRunning);

            var window = new SetupWizardWindow { DataContext = root };
            HeadlessThemeHelper.Apply(window);
            await root.GoToAsync(3);

            var grid = Assert.IsType<Grid>(window.Content);
            var bodyGrid = Assert.IsType<Grid>(grid.Children[3]);
            var stepView = Assert.IsType<WizardBenchmarkStepView>(Assert.IsType<Border>(bodyGrid.Children[3]).Child);

            // The live summary line renders the pinned "{done} / {N} target(s) finished ·
            // {target}: {message}" text from the VM data (bound, resolved headlessly).
            Assert.Contains(
                stepView.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text),
                t => t == "0 / 1 target(s) finished · Local: Downloading qwen3:8b (50% · 2.1 GB / 4.3 GB)");

            // The overall bar (Height 8) advances fractionally (pinned 35.0 = (0 + 0.2 +
            // 0.3·0.5) / 1 for the sole downloading target) and is determinate + visible.
            var overall = stepView.GetLogicalDescendants().OfType<ProgressBar>().Single(p => p.Height == 8);
            Assert.Equal(35.0, overall.Value);
            Assert.False(overall.IsIndeterminate);
            Assert.True(overall.IsVisible);

            // The per-row DataTemplate carries the live progress contract: the detail line
            // (ProgressDetailText, visible only with live progress) + the slim fractional
            // bar (RowProgressPercent / IsIndeterminate, visible on any running row).
            var itemsControl = stepView.GetLogicalDescendants().OfType<ItemsControl>()
                .Single(ic => BindingPath(ic, ItemsControl.ItemsSourceProperty) == "BenchmarkRows");
            var built = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate).Build(null);
            var cardStack = Assert.IsType<StackPanel>(Assert.IsType<Border>(built).Child);

            var detail = cardStack.Children.OfType<TextBlock>().Single(tb =>
                BindingPath(tb, TextBlock.TextProperty) == "ProgressDetailText");
            Assert.Equal("HasLiveProgress", BindingPath(detail, Control.IsVisibleProperty));

            var bar = cardStack.Children.OfType<ProgressBar>().Single(p =>
                BindingPath(p, ProgressBar.ValueProperty) == "RowProgressPercent");
            Assert.Equal(4.0, bar.Height);
            Assert.Equal("IsIndeterminate", BindingPath(bar, ProgressBar.IsIndeterminateProperty));
            Assert.Equal("IsRunningRow", BindingPath(bar, Control.IsVisibleProperty));

            await root.DisposeAsync();
        });
    }

    /// <summary>
    /// The XAML path of the binding on <paramref name="property"/> (null when the property
    /// carries no binding) — the <see cref="InstallTargetsStepViewLayoutTests"/> precedent for
    /// asserting compiled-template binding contracts headlessly.
    /// </summary>
    private static string? BindingPath(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Description")?.GetValue(expression) as string;
    }

    [Fact]
    public async Task Step5_RecommendationsView_HasOneClickGenerateAndApplyAndRoleCards()
    {
        await _fixture.DispatchAsync(() =>
        {
            var stepView = GetStepView<WizardRecommendationsStepView>(4);
            var buttons = stepView.GetLogicalDescendants().OfType<Button>()
                .Select(b => b.Content as string ?? string.Empty)
                .ToArray();
            Assert.Equal(["Generate & Apply", "Generate", "Apply", "Go to benchmark"], buttons);
            var texts = stepView.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Roles", texts);
            Assert.Contains("Models", texts);
            Assert.Contains("Applied ceilings", texts);
        });
    }

    [Fact]
    public async Task Root_BackgroundBusyAndGoToAsync_WithBoundWindow_NeverThrowsCrossThreadAccess()
    {
        await _fixture.DispatchAsync(async () =>
        {
            var root = BuildRootViewModel();
            var window = new SetupWizardWindow { DataContext = root };
            HeadlessThemeHelper.Apply(window);

            // Simulate a background-thread property update and async navigation on a bound window
            await Task.Run(() =>
            {
                root.EnvironmentStep.IsBusy = true;
                root.EnvironmentStep.IsBusy = false;
            });

            await root.NextCommand.ExecuteAsync(null);
            Assert.Equal(1, root.SelectedStep);
            await root.BackCommand.ExecuteAsync(null);
            Assert.Equal(0, root.SelectedStep);
            await root.DisposeAsync();
        });
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static Grid GetWindowGrid()
    {
        var window = new SetupWizardWindow { DataContext = BuildRootViewModel() };
        HeadlessThemeHelper.Apply(window);
        return Assert.IsType<Grid>(window.Content);
    }

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
            NonLoopbackLocalAddresses = () => [],
            LanBindProbe = (_, _) => null,
            LaunchctlGetenv = _ => null,
        };
        var environment = new WizardEnvironmentStepViewModel(configurator, connection);

        Task GatedDelay(TimeSpan _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        WizardTargetsStepViewModel targets = null!;
        targets = new WizardTargetsStepViewModel(() => fake, ct => targets.RefreshAsync(ct), GatedDelay);
        var cloud = new WizardCloudStepViewModel(() => fake);
        WizardBenchmarkStepViewModel bench = null!;
        bench = new WizardBenchmarkStepViewModel(() => fake, ct => bench.RefreshAsync(ct), GatedDelay);
        SetupWizardViewModel root = null!;
        var rec = new WizardRecommendationsStepViewModel(() => fake, () => _ = root.GoToAsync(3));
        root = new SetupWizardViewModel(connection, environment, targets, cloud, bench, rec);
        return root;
    }

    private static TView GetStepView<TView>(int stepIndex) where TView : Control
    {
        var window = new SetupWizardWindow { DataContext = BuildRootViewModel() };
        HeadlessThemeHelper.Apply(window);
        var grid = Assert.IsType<Grid>(window.Content);
        var bodyGrid = Assert.IsType<Grid>(grid.Children[3]);
        var stepPane = Assert.IsType<Border>(bodyGrid.Children[stepIndex]);
        return Assert.IsType<TView>(stepPane.Child);
    }

    private static (WizardCloudStepView View, SetupWizardViewModel Root) GetStep3View()
    {
        var root = BuildRootViewModel();
        var window = new SetupWizardWindow { DataContext = root };
        HeadlessThemeHelper.Apply(window);
        var grid = Assert.IsType<Grid>(window.Content);
        var bodyGrid = Assert.IsType<Grid>(grid.Children[3]);
        var step3Pane = Assert.IsType<Border>(bodyGrid.Children[2]);
        return (Assert.IsType<WizardCloudStepView>(step3Pane.Child), root);
    }

    private static List<Border> FindSectionCards(Avalonia.Visual root)
    {
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

        public Task DispatchAsync(Func<Task> func) =>
            Session.Dispatch(async () => { await func(); return true; }, CancellationToken.None);

        public Task<TResult> DispatchAsync<TResult>(Func<TResult> func) =>
            Session.Dispatch(func, CancellationToken.None);

        private HeadlessUnitTestSession Session => _session!;
    }
}
