using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App.Views.InstallModelWizard;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Install Model wizard / Targets step layout contract tests (task #144): the row
/// DataTemplate (compiled bindings — <c>x:DataType="vm:InstallTargetRowVm"</c>) must carry
/// the per-row RE-PROBE button (visible only via <c>ShowReprobe</c>, gated by
/// <c>CanReprobe</c>, busy content via <c>ReprobeButtonContent</c>) right after the
/// <c>HardwareText</c> line in the row's identity strip, so unprobed rows can trigger the
/// daemon-side hardware probe manually. Structural assertions only —
/// <see cref="InstallTargetsStepView"/> is a <see cref="UserControl"/>, so construction needs
/// no windowing platform (the <see cref="MonitorViewLayoutTests"/> precedent).
/// </summary>
/// <remarks>
/// [Collection("Avalonia")]: this class creates <see cref="InstallTargetsStepView"/> control
/// trees, so it must never run in parallel with the other Avalonia-control-creating test
/// classes — Avalonia 12 keeps process-wide mutable state (the thread-unsafe
/// AvaloniaPropertyDictionaryPool static stack and the first-touch-owned Dispatcher.UIThread),
/// which corrupts under concurrent control construction.
/// </remarks>
[Collection("Avalonia")]
public sealed class InstallTargetsStepViewLayoutTests
{
    [Fact]
    public void RowTemplate_IdentityStrip_ContainsReProbeButtonAfterHardwareText()
    {
        var identityStrip = LoadIdentityStrip();

        var reProbe = identityStrip.Children.OfType<Button>().Single(b =>
            BindingPath(b, Button.ContentProperty) == "ReprobeButtonContent");

        // The transient busy-state content + the visibility/gating bindings of the
        // unprobed-row affordance.
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(reProbe, Button.ContentProperty));
        Assert.Equal("ReprobeButtonContent", BindingPath(reProbe, Button.ContentProperty));
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(reProbe, Button.IsVisibleProperty));
        Assert.Equal("ShowReprobe", BindingPath(reProbe, Button.IsVisibleProperty));
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(reProbe, Button.IsEnabledProperty));
        Assert.Equal("CanReprobe", BindingPath(reProbe, Button.IsEnabledProperty));
        // The tooltip is a LITERAL (not a binding) — assert its value directly.
        Assert.Equal(
            "Run the daemon-side hardware probe for this target now",
            reProbe.GetValue(ToolTip.TipProperty));

        // The button sits right AFTER the HardwareText line in the identity strip.
        var children = identityStrip.Children.ToArray();
        var hardwareText = children.OfType<TextBlock>().Single(tb =>
            BindingPath(tb, TextBlock.TextProperty) == "HardwareText");
        Assert.Equal(Array.IndexOf(children, hardwareText) + 1, Array.IndexOf(children, reProbe));
    }

    private static StackPanel LoadIdentityStrip()
    {
        var view = new InstallTargetsStepView();
        view.ApplyTemplate();
        var all = AllControls(view).ToList();

        var itemsControl = all.OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == "Rows");
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        var built = template.Build(null);

        var row = Assert.IsType<StackPanel>(Assert.IsType<Border>(built).Child);
        // The identity strip is the row's first (horizontal, checkbox-leading) StackPanel.
        return row.Children.OfType<StackPanel>().Single(sp =>
            sp.Children.OfType<CheckBox>().Any() &&
            sp.Children.OfType<TextBlock>().Any(tb => BindingPath(tb, TextBlock.TextProperty) == "HardwareText"));
    }

    /// <summary>
    /// Returns the XAML path of the binding on <paramref name="property"/>, or null when the
    /// property carries no binding. Avalonia 12's concrete <c>BindingExpression</c> type is
    /// internal, so the human-readable <c>Description</c> is read reflectively (the
    /// <see cref="MonitorViewLayoutTests"/> precedent).
    /// </summary>
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
