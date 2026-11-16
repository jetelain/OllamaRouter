using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="ProportionalPanel"/> math tests (task #114): relative-share normalization
/// (zero weights, single 100%, multiple targets, &gt;100% sums and float drift still fill the
/// track exactly, negative weights → 0) and the <see cref="ProportionalPanel.ResolveWeight"/>
/// container-unwrapping fallback. The unwrapping tests cover the real Avalonia 12 pipeline,
/// where <c>ItemsControl</c> wraps <c>DataTemplate</c> items in a <c>ContentPresenter</c> whose
/// content is the data item and whose visual child is the template root carrying the weight.
/// </summary>
/// <remarks>
/// [Collection("Avalonia")] (task #129): creates real Avalonia controls, so it must not run in
/// parallel with the other Avalonia-control-creating test classes (shared process-wide
/// dispatcher/property-pool state).
/// </remarks>
[Collection("Avalonia")]
public sealed class ProportionalPanelTests
{
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(-5, 100, 0)]
    [InlineData(50, 0, 0)]
    [InlineData(50, -100, 0)]
    [InlineData(25, 100, 0.25)]
    [InlineData(100, 100, 1.0)]
    public void SegmentShare_ReturnsRelativeShare_WithZeroGuards(double weight, double totalWeight, double expected)
    {
        Assert.Equal(expected, ProportionalPanel.SegmentShare(weight, totalWeight), 10);
    }

    [Fact]
    public void ComputeSegmentWidths_AllZeroWeights_YieldsAllZero()
    {
        var widths = ProportionalPanel.ComputeSegmentWidths([0.0, 0.0, 0.0], 400);

        Assert.Equal([0.0, 0.0, 0.0], widths);
    }

    [Fact]
    public void ComputeSegmentWidths_SingleFullShare_FillsTheTrack()
    {
        var widths = ProportionalPanel.ComputeSegmentWidths([100.0], 400);

        Assert.Equal([400.0], widths);
    }

    [Fact]
    public void ComputeSegmentWidths_MultipleTargets_AreProportional()
    {
        var widths = ProportionalPanel.ComputeSegmentWidths([25.0, 75.0], 400);

        Assert.Equal(100.0, widths[0], 10);
        Assert.Equal(300.0, widths[1], 10);
    }

    [Fact]
    public void ComputeSegmentWidths_SumOver100_IsNormalized_AndStillFillsTheTrack()
    {
        var widths = ProportionalPanel.ComputeSegmentWidths([30.0, 80.0], 100);

        Assert.Equal(30.0 / 110.0 * 100, widths[0], 10);
        Assert.Equal(80.0 / 110.0 * 100, widths[1], 10);
        Assert.Equal(100.0, widths.Sum(), 10);
    }

    [Fact]
    public void ComputeSegmentWidths_FloatDrift_SumSlightlyBelow100_StillFillsTheTrack()
    {
        var widths = ProportionalPanel.ComputeSegmentWidths([60.0, 39.99], 1000);

        Assert.Equal(1000.0, widths.Sum(), 10);
    }

    [Fact]
    public void ComputeSegmentWidths_NegativeWeights_YieldZeroWidths_AndAreExcludedFromTheTotal()
    {
        var widths = ProportionalPanel.ComputeSegmentWidths([-1.0, 49.0, 50.0], 100);

        Assert.Equal(0.0, widths[0]);
        Assert.Equal(49.0 / 99.0 * 100, widths[1], 10);
        Assert.Equal(50.0 / 99.0 * 100, widths[2], 10);
    }

    [Fact]
    public void ResolveWeight_DirectWeightOnChild_IsReturned()
    {
        var border = new Border();
        ProportionalPanel.SetWeight(border, 42.5);

        Assert.Equal(42.5, ProportionalPanel.ResolveWeight(border));
    }

    [Fact]
    public void ResolveWeight_ContainerWrappingWeightedContent_FallsBackToContentWeight()
    {
        var inner = new Border();
        ProportionalPanel.SetWeight(inner, 17.9);
        var container = new ContentControl { Content = inner };

        Assert.Equal(17.9, ProportionalPanel.ResolveWeight(container));
    }

    [Fact]
    public void ResolveWeight_NoWeightAnywhere_ReturnsZero()
    {
        var container = new ContentControl { Content = new Border() };

        Assert.Equal(0.0, ProportionalPanel.ResolveWeight(container));
        Assert.Equal(0.0, ProportionalPanel.ResolveWeight(new Border()));
    }

    [Fact]
    public void ResolveWeight_NegativeWeight_ReturnsZero()
    {
        var border = new Border();
        ProportionalPanel.SetWeight(border, -3);

        Assert.Equal(0.0, ProportionalPanel.ResolveWeight(border));
    }

    [Fact]
    public void ResolveWeight_ItemsControlContentPresenterContainer_FindsWeightOnTemplateRoot()
    {
        // Avalonia 12 ItemsControl.CreateContainerForItemOverride returns a ContentPresenter
        // whose Content is the data item (not a Control); the DataTemplate root — the only
        // place the XAML can bind the attached Weight — becomes the presenter's visual child.
        var presenter = CreateItemsControlContainer(item: "some data item", percentage: 33.3);

        Assert.Equal(33.3, ProportionalPanel.ResolveWeight(presenter), 10);
    }

    [Fact]
    public void ResolveWeight_ContentPresenterWithoutChildren_ReturnsZero()
    {
        var presenter = new ContentPresenter { Content = "data item" };

        Assert.Equal(0.0, ProportionalPanel.ResolveWeight(presenter));
    }

    [Fact]
    public void ArrangeOverride_ItemsControlPresenterPipeline_SegmentsAreProportional()
    {
        // Regression for the original task #114 defect: the panel's children are the
        // ItemsControl containers (ContentPresenters), so the weights bound inside the
        // DataTemplate must still be discovered and used for the arranged widths.
        var panel = new ProportionalPanel();
        panel.Children.Add(CreateItemsControlContainer("Local", 25.0));
        panel.Children.Add(CreateItemsControlContainer("Cloud", 75.0));

        panel.Measure(new Size(400, 18));
        panel.Arrange(new Rect(0, 0, 400, 18));

        Assert.Equal(100.0, panel.Children[0].Bounds.Width, 2);
        Assert.Equal(300.0, panel.Children[1].Bounds.Width, 2);
    }

    /// <summary>
    /// Builds the container structure exactly as Avalonia 12's <c>ItemsControl</c> does:
    /// a <see cref="ContentPresenter"/> with the data item as content and the template root
    /// (carrying the attached weight) instantiated as its visual child via <c>UpdateChild</c>
    /// — the same public call the framework makes when the container attaches.
    /// </summary>
    private static ContentPresenter CreateItemsControlContainer(string item, double percentage)
    {
        var presenter = new ContentPresenter
        {
            Content = item,
            ContentTemplate = new FuncDataTemplate<string>((_, _) =>
            {
                var segment = new Border();
                ProportionalPanel.SetWeight(segment, percentage);
                return segment;
            }),
        };
        presenter.UpdateChild();
        return presenter;
    }
}
