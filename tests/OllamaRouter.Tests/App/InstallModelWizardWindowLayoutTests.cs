using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using OllamaRouter.App.ViewModels.InstallModelWizard;
using OllamaRouter.App.Views.InstallModelWizard;
using OllamaRouter.Endpoints.RouterApi.Fleet;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Install Model wizard window layout contract tests (task #172, NightCodeForge Installer
/// shell parity): the floating 4-row <see cref="Grid"/> card shell (branding header with the
/// two-tone OllamaRouter title + the model-reference chip, the centered 3-step strip with
/// BOTH the <c>WizardStepForegroundConverter</c> and <c>IntEqualityToFontWeightConverter</c>
/// bindings, the three step panes, and the footer with all six navigation buttons + their
/// visibility gates). Construction needs a windowing platform, so everything runs on the
/// headless Avalonia session fixture in the exclusive "Avalonia" xunit collection (the
/// <see cref="SetupWizardWindowLayoutTests"/> pattern).
/// </summary>
[Collection("Avalonia")]
public sealed class InstallModelWizardWindowLayoutTests :
    IClassFixture<InstallModelWizardWindowLayoutTests.HeadlessSessionFixture>
{
    private readonly HeadlessSessionFixture _fixture;

    public InstallModelWizardWindowLayoutTests(HeadlessSessionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Root_FirstRow_IsBrandingHeader_LogoTwoToneNameAndModelReferenceChip()
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
            var subtitle = Assert.IsType<TextBlock>(titleStack.Children[1]);
            Assert.Equal("Install a model onto fleet targets", subtitle.Text);

            // The optional model-reference chip: hidden while the reference is empty.
            var chip = Assert.IsType<Border>(headerGrid.Children[2]);
            var chipText = Assert.IsType<TextBlock>(chip.Child);
            Assert.Equal("ModelStep.ModelReference", BindingPath(chipText, TextBlock.TextProperty));
            Assert.Equal("ModelStep.ModelReference", BindingPath(chip, Control.IsVisibleProperty));
        });
    }

    [Fact]
    public async Task Root_StepStrip_InRow1_KeepsThreeStepLabelsCentered_WithBothConverters()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetWindowGrid();

            var strip = Assert.IsType<Border>(grid.Children[1]);
            Assert.Equal(1, Grid.GetRow(strip));
            var panel = Assert.IsType<StackPanel>(strip.Child);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, panel.HorizontalAlignment);
            var labels = panel.Children.OfType<TextBlock>().ToArray();
            Assert.Equal(
                ["1 · Model", "2 · Targets", "3 · Review & Install"],
                labels.Select(t => t.Text));

            // EVERY label carries BOTH the active-weight binding and the completion-
            // colored foreground binding onto SelectedStep.
            foreach (var label in labels)
            {
                Assert.Equal("SelectedStep", BindingPath(label, TextBlock.FontWeightProperty));
                Assert.Equal("SelectedStep", BindingPath(label, TextBlock.ForegroundProperty));
            }
        });
    }

    [Fact]
    public async Task Root_Body_InRow2_HasThreeStepPanes_BoundToTheStepViewModels()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetWindowGrid();
            var body = Assert.IsType<Grid>(grid.Children[2]);
            Assert.Equal(2, Grid.GetRow(body));

            var panes = body.Children.OfType<Border>().ToArray();
            Assert.Equal(3, panes.Length);

            var stepViewTypes = new[]
            {
                typeof(InstallModelStepView),
                typeof(InstallTargetsStepView),
                typeof(InstallReviewStepView)
            };
            var expectedDataBindings = new[] { "ModelStep", "TargetsStep", "ReviewStep" };

            for (var i = 0; i < 3; i++)
            {
                Assert.Equal("SelectedStep", BindingPath(panes[i], Control.IsVisibleProperty));
                var view = panes[i].Child!;
                Assert.IsAssignableFrom(stepViewTypes[i], view);
                Assert.Equal(expectedDataBindings[i], BindingPath(view, StyledElement.DataContextProperty));
            }
        });
    }

    [Fact]
    public async Task Root_Footer_InRow3_HasTitleAndAllSixNavigationButtonsWithGates()
    {
        await _fixture.DispatchAsync(() =>
        {
            var grid = GetWindowGrid();

            var footer = Assert.IsType<Border>(grid.Children[3]);
            Assert.Equal(3, Grid.GetRow(footer));
            var footerGrid = Assert.IsType<Grid>(footer.Child);
            var footerTitle = Assert.IsType<TextBlock>(footerGrid.Children[0]);
            Assert.Equal("Install Model Wizard", footerTitle.Text);

            var buttons = footerGrid.Children[1].GetLogicalDescendants().OfType<Button>()
                .ToArray();
            Assert.Equal(
                ["Cancel", "Back", "Next", "Install", "Cancel install", "Done"],
                buttons.Select(b => b.Content as string ?? string.Empty));

            var byContent = buttons.ToDictionary(b => (string)b.Content!);
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(byContent["Install"], Button.CommandProperty));
            Assert.Equal("ReviewStep.IsRunning", BindingPath(byContent["Install"], Control.IsVisibleProperty));
            Assert.Equal("ReviewStep.IsRunning", BindingPath(byContent["Cancel install"], Control.IsVisibleProperty));
            Assert.Equal("ReviewStep.RunCompleted", BindingPath(byContent["Done"], Control.IsVisibleProperty));
        });
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static Grid GetWindowGrid()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([])
        };
        var window = new InstallModelWizardWindow(new InstallModelWizardViewModel(() => fake));
        HeadlessThemeHelper.Apply(window);
        return Assert.IsType<Grid>(window.Content);
    }

    private static string? BindingPath(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Description")?.GetValue(expression) as string;
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
