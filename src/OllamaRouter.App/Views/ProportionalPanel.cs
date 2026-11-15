using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace OllamaRouter.App.Views;

/// <summary>
/// A horizontal <see cref="Panel"/> that arranges its children side-by-side with widths
/// proportional to an attached <see cref="WeightProperty"/> (task #114: the 7-day target
/// usage-ratio segmented bar on the Monitor tab). Segment widths are normalized by RELATIVE
/// share (<c>weight / sum(weights)</c> × track width), so floating-point percentage drift
/// (e.g. 99.99 or 100.01 sums) and any >100% input still fill the track exactly, while
/// zero/negative weights yield a zero width (the segment is not painted — the same rule as
/// the web monitor's <c>addSeg</c> skip).
/// </summary>
public sealed class ProportionalPanel : Panel
{
    /// <summary>
    /// Attached weight property; the XAML sets it per segment from <c>Percentage</c>:
    /// <c>views:ProportionalPanel.Weight="{Binding Percentage}"</c>.
    /// </summary>
    public static readonly AttachedProperty<double> WeightProperty =
        AvaloniaProperty.RegisterAttached<ProportionalPanel, double>("Weight", typeof(ProportionalPanel), defaultValue: 0.0);

    /// <summary>Reads the <see cref="WeightProperty"/> from <paramref name="control"/>.</summary>
    public static double GetWeight(Control control) => control.GetValue(WeightProperty);

    /// <summary>Writes the <see cref="WeightProperty"/> on <paramref name="control"/>.</summary>
    public static void SetWeight(Control control, double value) => control.SetValue(WeightProperty, value);

    /// <summary>
    /// The share of the track a single weight occupies: 0 for non-positive weight or total,
    /// otherwise <c>weight / totalWeight</c> (result in (0, 1]).
    /// </summary>
    internal static double SegmentShare(double weight, double totalWeight)
        => weight <= 0 || totalWeight <= 0 ? 0 : weight / totalWeight;

    /// <summary>
    /// The pixel width for each weight given the total available width; widths of a positive
    /// weight set always sum to <paramref name="totalWidth"/> (relative-share normalization).
    /// </summary>
    internal static double[] ComputeSegmentWidths(IReadOnlyList<double> weights, double totalWidth)
    {
        var totalWeight = 0.0;
        foreach (var weight in weights)
            totalWeight += Math.Max(0, weight);
        return weights.Select(w => SegmentShare(w, totalWeight) * totalWidth).ToArray();
    }

    /// <summary>
    /// Resolves the effective weight of a panel child. <c>ItemsControl</c> generates a wrapper
    /// container per item between this panel and the <c>DataTemplate</c> root — in Avalonia 12
    /// that is a <c>ContentPresenter</c> whose <c>Content</c> is the data item, so a weight bound
    /// inside the template (on the template root) is not visible on the container itself. The
    /// resolution order is: (1) the weight on the child directly, (2) the weight on the
    /// content child when the child is a <see cref="ContentControl"/> whose content is itself a
    /// <see cref="Control"/>, (3) a depth-first walk of the child's visual tree, where the
    /// template root lives (the <c>ContentPresenter</c> adds it as a visual child once its
    /// content template is applied). Negative or absent weights resolve to 0.
    /// </summary>
    internal static double ResolveWeight(Control child)
    {
        var weight = GetWeight(child);
        if (weight > 0)
            return weight;
        if (child is ContentControl { Content: Control contentChild })
        {
            var contentWeight = GetWeight(contentChild);
            if (contentWeight > 0)
                return contentWeight;
        }
        foreach (var visual in child.GetVisualChildren())
        {
            if (visual is Control visualChild)
            {
                var descendantWeight = ResolveWeight(visualChild);
                if (descendantWeight > 0)
                    return descendantWeight;
            }
        }
        return 0;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(availableSize);
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = ComputeSegmentWidths(Children.Select(ResolveWeight).ToArray(), finalSize.Width);
        var x = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var width = widths[i];
            // The last POSITIVE segment absorbs floating-point accumulation so its right edge
            // lands exactly on the track edge (child margins are deflated by Avalonia's
            // Control.Arrange plumbing, so the right margin renders as a dark-track gap).
            var right = i == Children.Count - 1 && width > 0 ? finalSize.Width : x + width;
            Children[i].Arrange(new Rect(x, 0, Math.Max(0, right - x), finalSize.Height));
            x = right;
        }
        return finalSize;
    }
}
