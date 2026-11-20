using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using OllamaRouter.App.Views;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Fleet &amp; Cloud Targets tab layout contract tests (task #144): the row DataTemplate
/// must carry the per-row 'Update hardware' button in the row's TOP-RIGHT action column
/// (BEFORE Edit — maintainer-confirmed placement; the hardware inspector Expander is
/// collapsed by default, so the action must stay visible) with the
/// <c>UpdateHardwareButtonContent</c> (Content) + <c>CanUpdateHardware</c> (IsEnabled)
/// bindings, and the row info strip (Grid.Column 3) must carry the <c>SshLabel</c>
/// TextBlock. Structural assertions only — <see cref="FleetTargetsView"/> is a
/// <see cref="UserControl"/>, so construction needs no windowing platform (the
/// <see cref="MonitorViewLayoutTests"/> precedent).
/// </summary>
/// <remarks>
/// [Collection("Avalonia")]: this class creates <see cref="FleetTargetsView"/> control trees,
/// so it must never run in parallel with the other Avalonia-control-creating test classes —
/// Avalonia 12 keeps process-wide mutable state (the thread-unsafe
/// AvaloniaPropertyDictionaryPool static stack and the first-touch-owned Dispatcher.UIThread),
/// which corrupts under concurrent control construction.
/// </remarks>
[Collection("Avalonia")]
public sealed class FleetTargetsViewLayoutTests
{
    [Fact]
    public void RowTemplate_ActionColumn_ContainsUpdateHardwareButtonBeforeEdit()
    {
        var (row, header) = LoadRowTemplate();

        // The row header grid's action column (Grid.Column 5) holds the button row.
        var actionPanel = Assert.IsType<StackPanel>(
            header.Children.Single(c => Grid.GetColumn(c) == 5));
        var buttons = actionPanel.Children.OfType<Button>().ToArray();

        // The 'Update hardware' button carries the BUSY-STATE binding (not a literal
        // Content); Edit / Delete keep their literal content (the task #102 row actions).
        var updateHardware = buttons.Single(b =>
            BindingPath(b, Button.ContentProperty) == "UpdateHardwareButtonContent");
        var edit = buttons.Single(b => b.Content is string s && s == "Edit");
        var delete = buttons.Single(b => b.Content is string s && s == "Delete");

        // Top-right action column, BEFORE Edit (maintainer-confirmed placement).
        Assert.True(Array.IndexOf(buttons, updateHardware) < Array.IndexOf(buttons, edit),
            "the 'Update hardware' button must precede Edit in the row action column");
        Assert.True(Array.IndexOf(buttons, edit) < Array.IndexOf(buttons, delete),
            "Edit must still precede Delete in the row action column");

        // The transient busy-state content + the ssh-probeable gating binding.
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(updateHardware, Button.ContentProperty));
        Assert.Equal("UpdateHardwareButtonContent", BindingPath(updateHardware, Button.ContentProperty));
        Assert.NotNull(BindingOperations.GetBindingExpressionBase(updateHardware, Button.IsEnabledProperty));
        Assert.Equal("CanUpdateHardware", BindingPath(updateHardware, Button.IsEnabledProperty));
        // The tooltip is a LITERAL (not a binding) — assert its value directly.
        Assert.Equal(
            "Re-run the daemon-side hardware probe for this target (overwrites the stored profile)",
            updateHardware.GetValue(ToolTip.TipProperty));
    }

    [Fact]
    public void RowTemplate_InfoStrip_CarriesTheSshLabel()
    {
        var (_, header) = LoadRowTemplate();

        // The row info strip (Grid.Column 3) carries the ssh credential label
        // (null text → invisible when the target has no sshUser).
        var infoStrip = Assert.IsType<StackPanel>(
            header.Children.Single(c => Grid.GetColumn(c) == 3));
        var sshLabel = infoStrip.Children.OfType<TextBlock>().Single(tb =>
            BindingPath(tb, TextBlock.TextProperty) == "SshLabel");

        Assert.NotNull(sshLabel);
    }

    private static (StackPanel row, Grid header) LoadRowTemplate()
    {
        var view = new FleetTargetsView();
        view.ApplyTemplate();
        var all = AllControls(view).ToList();

        var itemsControl = all.OfType<ItemsControl>().Single(ic =>
            BindingPath(ic, ItemsControl.ItemsSourceProperty) == "Rows");
        var template = Assert.IsType<DataTemplate>(itemsControl.ItemTemplate);
        var built = template.Build(null);

        var row = Assert.IsType<StackPanel>(Assert.IsType<Border>(built).Child);
        var header = row.Children.OfType<Grid>().Single();
        return (row, header);
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
