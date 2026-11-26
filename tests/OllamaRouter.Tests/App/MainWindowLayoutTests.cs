using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="MainWindow"/> header layout contract tests (task #173): the status pill's
/// <see cref="Ellipse"/> fill must be BOUND to the view model's <c>DaemonStatus</c>
/// (via the <c>DaemonStatusToBrushConverter</c> — no more hardcoded green), and the
/// pill's status-text / detail <see cref="TextBlock"/>s keep their <c>StatusText</c> /
/// <c>StatusDetail</c> bindings. The window is constructed UNSHOWN on a headless session
/// (a <see cref="Window"/> needs the windowing platform); all tree access is logical-tree
/// (visual children never materialize for unshown windows) and dispatched on the UI thread.
/// </summary>
[Collection("Avalonia")]
public sealed class MainWindowLayoutTests :
    IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>, IAsyncLifetime
{
    private readonly PreloadDialogLayoutTests.HeadlessSessionFixture _fixture;
    private readonly FakeRouterApiClient _client = new();
    private readonly FakeDaemonLauncher _launcher = new();
    private readonly AppConnectionHarness _harness;

    public MainWindowLayoutTests(PreloadDialogLayoutTests.HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
        _harness = new AppConnectionHarness(new FakeDefaultKeyReader(), () => _client, () => _launcher);
    }

    private MainWindow? _window;
    private MainWindowViewModel? _viewModel;

    private static Task GatedDelay(TimeSpan span, CancellationToken ct) =>
        new Task(static () => { }, ct);

    [Fact]
    public async Task StatusPill_EllipseFill_IsBoundToDaemonStatus_WithTheStatusBrushConverter()
    {
        await BuildMainWindow();

        await _fixture.DispatchAsync(() =>
        {
            var ellipse = _window!.GetLogicalDescendants().OfType<Ellipse>().Single();

            var expression = BindingOperations.GetBindingExpressionBase(ellipse, Ellipse.FillProperty);
            Assert.NotNull(expression);
            Assert.Equal("DaemonStatus", BindingPath(ellipse, Ellipse.FillProperty));
            Assert.True(ReferenceEquals(
                       BindingConverter(ellipse, Ellipse.FillProperty),
                       OllamaRouter.App.Converters.DaemonStatusToBrushConverter.Instance),
                "the status dot fill must route through the DaemonStatusToBrushConverter");
        });
    }

    [Fact]
    public async Task StatusPill_TextAndDetailBindings_ArePreserved()
    {
        await BuildMainWindow();

        await _fixture.DispatchAsync(() =>
        {
            // The pill: Border (TagBorder) > StackPanel > [Ellipse, status TextBlock]; the
            // detail TextBlock is the pill Border's sibling inside the title StackPanel.
            var statusDot = _window!.GetLogicalDescendants().OfType<Ellipse>().Single();
            var pillPanel = Assert.IsType<StackPanel>(statusDot.Parent);
            var pill = Assert.IsType<Border>(pillPanel.Parent);
            var titlePanel = Assert.IsType<StackPanel>(pill.Parent);

            var statusText = pillPanel.Children.OfType<TextBlock>().Single();
            Assert.Equal("StatusText", BindingPath(statusText, TextBlock.TextProperty));

            var detailText = titlePanel.Children.OfType<TextBlock>().Single(t =>
                BindingPath(t, TextBlock.TextProperty) == "StatusDetail");
            Assert.Equal("StatusDetail", BindingPath(detailText, TextBlock.TextProperty));
        });
    }

    private async Task BuildMainWindow()
    {
        await _fixture.DispatchAsync(() =>
        {
            _launcher.Status = FakeDaemonLauncher.RunningStatus();
            var connection = _harness.Service;
            _viewModel = new MainWindowViewModel(
                connection,
                new MonitorViewModel(() => _client, GatedDelay),
                new AgentSessionsViewModel(() => _client, GatedDelay),
                new FleetTargetsViewModel(() => _client, GatedDelay),
                new CartographyViewModel(() => _client, GatedDelay),
                new SettingsViewModel(() => _client, GatedDelay));

            _window = new MainWindow();
            HeadlessThemeHelper.Apply(_window);
            _window.DataContext = _viewModel;
        });

        // One pump so the (compiled) bindings settle before the assertions.
        await _fixture.DispatchAsync(static () => { });
    }

    private static string? BindingPath(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Description")?.GetValue(expression) as string;
    }

    /// <summary>
    /// Reads the (internal) binding expression's <c>Converter</c> reflectively — the
    /// compiled XAML binding carries the <c>{x:Static …}</c> converter instance on it.
    /// </summary>
    private static object? BindingConverter(Control element, AvaloniaProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.GetType().GetProperty("Converter")?.GetValue(expression);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_viewModel is not null)
        {
            await _fixture.DispatchAsync(async () =>
            {
                await _viewModel.DisposeAsync();
            });
        }

        _harness.Dispose();
    }
}
